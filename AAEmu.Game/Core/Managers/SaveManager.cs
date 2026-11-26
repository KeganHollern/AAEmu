using System.Diagnostics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks;
using AAEmu.Game.Models.Tasks.SaveTask;
using AAEmu.Game.Services.Health;

using Microsoft.Extensions.Hosting;
using NLog;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers;

public class SaveManager(
    ITaskManager taskManager,
    IHousingManager housingManager,
    IMailManager mailManager,
    IItemManager itemManager,
    IAuctionManager auctionManager,
    ICrimeManager crimeManager,
    IWorldManager worldManager,
    IZoneManager zoneManager,
    IShipyardManager shipyardManager = null,
    IHostApplicationLifetime hostLifetime = null) : Singleton<SaveManager>, ISaveManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private double Delay = 1;
    private bool _enabled = false;
    private bool _isSaving = false;
    private bool _consistencyFailed;
    internal Action<MySqlTransaction> CommitTransaction { get; set; } = transaction => transaction.Commit();
    internal Action<string, Exception> StopForConsistencyFailure { get; set; } = Environment.FailFast;
    internal static object PersistenceSyncRoot { get; } = new();
    private SaveTickStartTask saveTask;
    private readonly Lock _countdownLock = new();
    private ShutdownTask _countdown;

    public ShutdownScheduleResult SetShutdownTime(DateTime? deadline)
    {
        lock (_countdownLock)
        {
            if (_countdown != null || deadline == null)
                return UpdateCountdown(deadline);
        }

        // A database preflight can take time. Check the selected countdown again after it finishes.
        if (!DoSave())
            return ShutdownScheduleResult.SaveFailed;
        lock (_countdownLock)
        {
            if (_countdown != null)
                return UpdateCountdown(deadline);

            var task = new ShutdownTask(deadline.Value.AddSeconds(5), worldManager, hostLifetime);
            if (!taskManager.Schedule(task, null, TimeSpan.FromSeconds(1)))
                return ShutdownScheduleResult.Stopping;
            _countdown = task;
            return ShutdownScheduleResult.Scheduled;
        }
    }

    private ShutdownScheduleResult UpdateCountdown(DateTime? deadline)
    {
        if (_countdown == null)
            return ShutdownScheduleResult.NoCountdown;
        if (deadline.HasValue)
            return _countdown.ChangeShutdownTime(deadline.Value)
                ? ShutdownScheduleResult.Rescheduled : ShutdownScheduleResult.Stopping;
        if (!_countdown.CancelCountdown(taskManager))
            return ShutdownScheduleResult.Stopping;
        _countdown = null;
        return ShutdownScheduleResult.Cancelled;
    }

    internal void FailForConsistency(Exception exception)
    {
        lock (PersistenceSyncRoot)
        {
            _consistencyFailed = true;
            _enabled = false;
            const string message = "Game persistence stopped after an uncertain commit or failed publication. Restart from durable state.";
            Logger.Fatal(exception, message);
            StopForConsistencyFailure(message, exception);
        }
    }

    internal void ThrowIfConsistencyFailed()
    {
        if (_consistencyFailed)
            throw new InvalidOperationException("Persistence is stopped after an unconfirmed commit.");
    }

    public void Initialize()
    {
        Logger.Info("Initialising Save Manager...");
        _enabled = true;
        Delay = AppConfiguration.Instance.World.AutoSaveInterval;
        SaveTickStart();
    }

    public async System.Threading.Tasks.Task StopAsync()
    {
        _enabled = false;
        if (saveTask == null)
        {
            return;
        }
        var result = await saveTask.CancelAsync();
        if (result)
        {
            saveTask = null;
        }
        if (DoSave())
            return;

        // Only a failed checkpoint before commit can be replayed.
        ThrowIfConsistencyFailed();
        Logger.Warn("Final database checkpoint failed before commit. Retrying once.");
        if (!DoSave())
        {
            ThrowIfConsistencyFailed();
            throw new InvalidOperationException("The final database checkpoint failed before commit on both attempts.");
        }
    }

    public void SaveTickStart()
    {
        // Logger.Warn("SaveTickStart: Started");
        saveTask = new SaveTickStartTask();
        taskManager.Schedule(saveTask, TimeSpan.FromMinutes(Delay), TimeSpan.FromMinutes(Delay));
    }

    public bool DoSave()
    {
        var stopWatch = Stopwatch.StartNew();
        var saved = false;
        try
        {
            var credited = new List<(Character Player, int Amount)>();
            lock (PersistenceSyncRoot)
            {
                saved = CommitPersistence([], context =>
                {
                    InstantGameManager.Instance.SavePendingMatchResults(context);
                    ArenaHonorAward.ClaimPending(context, worldManager.GetAllCharacters(), credited);
                    housingManager.Save(context);
                    crimeManager.Save(context.Connection, context.Transaction);
                    shipyardManager?.Save(context);
                    zoneManager.Save(context.Connection, context.Transaction);
                    foreach (var world in worldManager.GetWorlds())
                    {
                        foreach (var slave in world.GetAllSlaves())
                        {
                            slave.Save(context.Connection, context.Transaction);
                        }
                    }
                });
                if (saved)
                    SendArenaHonorChanges(credited);
            }
            return saved;
        }
        catch (Exception exception)
        {
            // The consistency latch prevents any retry after an unconfirmed commit.
            Logger.Error(exception, "Database save did not complete.");
            return false;
        }
        finally
        {
            var elapsed = stopWatch.Elapsed;
            GameWorkMetrics.Default.RecordDatabaseSave(elapsed, saved);
            Logger.Debug("Saving data took {0}", elapsed);
        }
    }

    /// <summary>
    /// Saves the staged settlement and all pending economic source state in one
    /// transaction. False means no commit was attempted; a commit exception is
    /// propagated because restoring prepared live state would assume an outcome.
    /// </summary>
    public bool TryCommitEconomy(IReadOnlyCollection<Character> participants, Action<PersistenceSaveContext> writeSettlement = null)
    {
        ArgumentNullException.ThrowIfNull(participants);
        return CommitPersistence(participants, writeSettlement);
    }

    internal bool TryCommitEconomy(IReadOnlyCollection<Character> participants,
        Action<PersistenceSaveContext> writeSettlement, Action<PersistenceSaveContext> validateSource)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(validateSource);
        return CommitPersistence(participants, writeSettlement, validateSource);
    }

    internal bool TryCommitMailArchive(Action<PersistenceSaveContext> validateSource, Action<PersistenceSaveContext> writeArchive)
    {
        ArgumentNullException.ThrowIfNull(validateSource);
        return CommitPersistence([], writeArchive, validateSource);
    }

    private bool CommitPersistence(IReadOnlyCollection<Character> participants, Action<PersistenceSaveContext> writeSettlement,
        Action<PersistenceSaveContext> validateSource = null)
    {
        return CommitPendingState(context =>
        {
            // Legacy auction mail needs its persisted source locked before pending saves can replace it.
            validateSource?.Invoke(context);
            mailManager.Save(context);
            itemManager.Save(context);
            auctionManager.Save(context);

            var characters = worldManager.GetAllCharacters().ToDictionary(character => character.Id);
            foreach (var character in participants)
            {
                ArgumentNullException.ThrowIfNull(character);
                characters[character.Id] = character;
            }
            foreach (var character in characters.Values)
            {
                if (character.IsDeleted)
                    continue;
                if (!character.Save(context))
                    throw new InvalidOperationException($"Failed to save character {character.Id} - {character.Name}.");
            }

            writeSettlement?.Invoke(context);
        });
    }

    private bool CommitPendingState(Action<PersistenceSaveContext> write)
    {
        lock (PersistenceSyncRoot)
        {
            ThrowIfConsistencyFailed();
            if (AAEmu.Game.Models.Game.Skills.SkillLaborBatch.Current is { IsCommitting: false } skillBatch)
                return skillBatch.Fail();
            if (_isSaving)
                return false;
            _isSaving = true;
            var commitAttempted = false;
            try
            {
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                var context = new PersistenceSaveContext(connection, transaction);
                try
                {
                    write(context);
                    commitAttempted = true;
                    CommitTransaction(transaction);
                    context.AcknowledgeCommit();
                    return true;
                }
                catch when (!commitAttempted)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception exception) when (!commitAttempted)
            {
                Logger.Error(exception, "Database checkpoint was aborted before commit; pending saves remain queued.");
                return false;
            }
            catch (Exception exception)
            {
                FailForConsistency(exception);
                throw;
            }
            finally
            {
                _isSaving = false;
            }
        }
    }

    public void SaveTick()
    {
        if (!_enabled)
        {
            Logger.Warn("Auto-Saving disabled, skipping ...");
            return;
        }
        DoSave();
    }

    internal void DeliverArenaHonor(IReadOnlyList<ArenaHonorAward> awards)
    {
        lock (PersistenceSyncRoot)
        {
            var connected = awards.Select(award => worldManager.GetCharacterById(award.CharacterId))
                .OfType<Character>().Distinct().ToArray();
            if (connected.Length != 0)
                ClaimArenaHonor(connected);
        }
    }

    internal void ClaimArenaHonorOnEntry(Character player)
    {
        // The normal character selection sends the full balance after this claim.
        ClaimArenaHonor([player], publish: false);
    }

    private void ClaimArenaHonor(IReadOnlyCollection<Character> players, bool publish = true)
    {
        // Character selection can still be loading persisted buffs, cooldowns and HP.
        // Write only this balance and its receipts, under the same gate as normal Honor changes.
        var credited = new List<(Character Player, int Amount)>();
        if (CommitPendingState(context => ArenaHonorAward.ClaimPending(context, players, credited)) && publish)
            SendArenaHonorChanges(credited);
    }

    private static void SendArenaHonorChanges(List<(Character Player, int Amount)> credited)
    {
        foreach (var (player, amount) in credited)
        {
            try
            {
                if (player.Connection?.ActiveChar == player)
                    player.SendPacket(new SCGamePointChangedPacket((byte)GamePointKind.Honor, amount));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not send the committed arena Honor change to character {0}.", player.Id);
            }
        }
    }

    public void SetAutoSaveInterval()
    {
        Delay = AppConfiguration.Instance.World.AutoSaveInterval;
    }
}
