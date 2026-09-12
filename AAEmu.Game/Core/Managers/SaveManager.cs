using System.Diagnostics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Tasks;
using AAEmu.Game.Models.Tasks.SaveTask;

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
    IZoneManager zoneManager) : Singleton<SaveManager>, ISaveManager
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
    public ShutdownTask ShutdownTask { get; set; } = null;

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
        // Do one final save here
        DoSave();
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
        try
        {
            return CommitPersistence([], context =>
            {
                housingManager.Save(context.Connection, context.Transaction);
                crimeManager.Save(context.Connection, context.Transaction);
                zoneManager.Save(context.Connection, context.Transaction);
                foreach (var world in worldManager.GetWorlds())
                {
                    foreach (var slave in world.GetAllSlaves())
                    {
                        slave.Save(context.Connection, context.Transaction);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            // The consistency latch prevents any retry after an unconfirmed commit.
            Logger.Error(exception, "Database save did not complete.");
            return false;
        }
        finally
        {
            Logger.Debug("Saving data took {0}", stopWatch.Elapsed);
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
        lock (PersistenceSyncRoot)
        {
            if (_consistencyFailed)
                throw new InvalidOperationException("Persistence is stopped after an unconfirmed commit.");
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
                        if (!character.Save(context))
                            throw new InvalidOperationException($"Failed to save character {character.Id} - {character.Name}.");
                    }

                    writeSettlement?.Invoke(context);
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
                _consistencyFailed = true;
                _enabled = false;
                const string message = "Game persistence stopped after an unconfirmed commit. Restart from durable state.";
                Logger.Fatal(exception, message);
                StopForConsistencyFailure(message, exception);
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

    public void SetAutoSaveInterval()
    {
        Delay = AppConfiguration.Instance.World.AutoSaveInterval;
    }
}
