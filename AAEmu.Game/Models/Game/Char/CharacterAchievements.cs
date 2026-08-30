using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Skills;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterAchievements
{
    private const string SavepointName = "character_achievements";

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly object _syncRoot = new();
    private readonly AchievementGameData _gameData;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _persistStateOverride;
    private readonly Dictionary<uint, uint> _recordAmounts = [];
    private readonly Dictionary<uint, uint> _achievementAmounts = [];
    private readonly Dictionary<uint, DateTime> _completionTimes = [];
    private readonly List<uint> _pendingCompletionIds = [];
    private readonly HashSet<uint> _pendingCompletionIdSet = [];

    public Character Owner { get; }

    public CharacterAchievements(
        Character owner,
        AchievementGameData gameData = null,
        TimeProvider timeProvider = null)
        : this(owner, gameData, timeProvider, null)
    {
    }

    internal CharacterAchievements(
        Character owner,
        AchievementGameData gameData,
        TimeProvider timeProvider,
        Func<bool> persistStateOverride)
    {
        Owner = owner;
        _gameData = gameData ?? AchievementGameData.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persistStateOverride = persistStateOverride;
    }

    public void Load(MySqlConnection connection)
    {
        lock (_syncRoot)
        {
            _recordAmounts.Clear();
            _achievementAmounts.Clear();
            _completionTimes.Clear();
            _pendingCompletionIds.Clear();
            _pendingCompletionIdSet.Clear();

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT `record_id`, `amount` FROM `character_achievement_records` WHERE `character_id` = @character_id";
                command.Parameters.AddWithValue("@character_id", Owner.Id);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        _recordAmounts[reader.GetUInt32("record_id")] = reader.GetUInt32("amount");
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT `achievement_id`, `completed_at` FROM `character_achievements` WHERE `character_id` = @character_id";
                command.Parameters.AddWithValue("@character_id", Owner.Id);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var completedAt = DateTime.SpecifyKind(reader.GetDateTime("completed_at"), DateTimeKind.Utc);
                        _completionTimes[reader.GetUInt32("achievement_id")] = completedAt;
                    }
                }
            }
        }
    }

    public void Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        KeyValuePair<uint, uint>[] records;
        KeyValuePair<uint, DateTime>[] completions;
        lock (_syncRoot)
        {
            records = _recordAmounts.ToArray();
            // The outer character transaction can preserve an exact pending completion time.
            // Only a direct transaction clears pending state because this method does not own the outer commit.
            completions = _completionTimes.ToArray();
        }

        var savepointCreated = false;
        try
        {
            ExecuteTransactionCommand(connection, transaction, $"SAVEPOINT `{SavepointName}`");
            savepointCreated = true;

            foreach (var (recordId, amount) in records)
            {
                using var command = connection.CreateCommand();
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO `character_achievement_records` (`character_id`, `record_id`, `amount`) " +
                    "VALUES (@character_id, @record_id, @amount) " +
                    "ON DUPLICATE KEY UPDATE `amount` = GREATEST(`amount`, @amount)";
                command.Parameters.AddWithValue("@character_id", Owner.Id);
                command.Parameters.AddWithValue("@record_id", recordId);
                command.Parameters.AddWithValue("@amount", amount);
                command.ExecuteNonQuery();
            }

            foreach (var (achievementId, completedAt) in completions)
            {
                using var command = connection.CreateCommand();
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO `character_achievements` (`character_id`, `achievement_id`, `completed_at`) " +
                    "VALUES (@character_id, @achievement_id, @completed_at) " +
                    "ON DUPLICATE KEY UPDATE `completed_at` = LEAST(`completed_at`, @completed_at)";
                command.Parameters.AddWithValue("@character_id", Owner.Id);
                command.Parameters.AddWithValue("@achievement_id", achievementId);
                command.Parameters.AddWithValue("@completed_at", completedAt);
                command.ExecuteNonQuery();
            }

            ExecuteTransactionCommand(connection, transaction, $"RELEASE SAVEPOINT `{SavepointName}`");
            savepointCreated = false;
        }
        catch
        {
            if (savepointCreated)
            {
                ExecuteTransactionCommand(connection, transaction, $"ROLLBACK TO SAVEPOINT `{SavepointName}`");
                ExecuteTransactionCommand(connection, transaction, $"RELEASE SAVEPOINT `{SavepointName}`");
            }

            throw;
        }
    }

    public void ReconcileAuthoritativeState(MySqlConnection connection = null)
    {
        lock (_syncRoot)
        {
            foreach (var achievementId in _completionTimes.Keys.ToArray())
                SetRecordMaximum(CharRecordKind.CompleteAchievement, achievementId, 0, 1);

            SetRecordMaximum(CharRecordKind.CharLevel, 0, 0, Owner.Level);
            SetRecordMaximum(CharRecordKind.PlayTime, 0, 0, GetFullHours(Owner.OnlineTime));

            if (Owner.Abilities != null)
            {
                for (var abilityId = AbilityType.Fight; abilityId <= AbilityType.Love; abilityId++)
                {
                    SetRecordMaximum(
                        CharRecordKind.AbilityLevel,
                        (uint)abilityId,
                        0,
                        Owner.Abilities.GetAbilityLevel(abilityId));
                }
            }

            Evaluate(_gameData.GetActiveAchievementIds(), null);

            if (_pendingCompletionIds.Count > 0)
            {
                var persisted = connection != null
                    ? TryPersistState(connection)
                    : _persistStateOverride != null && TryPersistState();
                if (persisted)
                    ClearPendingCompletions();
            }
        }
    }

    public void UpdateLevel(byte level)
    {
        UpdateMaximum(CharRecordKind.CharLevel, 0, 0, level);
    }

    public void UpdateAbilityLevel(AbilityType abilityId, byte level)
    {
        UpdateMaximum(CharRecordKind.AbilityLevel, (uint)abilityId, 0, level);
    }

    public void UpdatePlayTime(TimeSpan onlineTime)
    {
        UpdateMaximum(CharRecordKind.PlayTime, 0, 0, GetFullHours(onlineTime));
    }

    public void UpdateMaximum(CharRecordKind kind, uint value1, uint value2, uint amount)
    {
        List<GamePacket> notifications = [];
        lock (_syncRoot)
        {
            var recordChanged = SetRecordMaximum(kind, value1, value2, amount, out var recordId);
            if (!recordChanged && _pendingCompletionIds.Count == 0)
                return;

            if (recordChanged)
                Evaluate(_gameData.GetAchievementIdsForRecord(recordId), notifications);

            if (_pendingCompletionIds.Count > 0 && CanPersistLive() && TryPersistState())
            {
                foreach (var achievementId in _pendingCompletionIds)
                {
                    notifications.Add(new SCAchievementCompletedPacket(
                        achievementId,
                        _completionTimes[achievementId]));
                }

                ClearPendingCompletions();
            }

            foreach (var notification in notifications)
                Owner.SendPacket(notification);
        }
    }

    public IReadOnlyList<SCAchievementsPacket> CreateSnapshotPackets()
    {
        List<AchievementInfo> entries;
        lock (_syncRoot)
        {
            entries = _gameData.GetActiveAchievementIds()
                .Select(achievementId => new AchievementInfo
                {
                    Id = achievementId,
                    Amount = _achievementAmounts.GetValueOrDefault(achievementId),
                    Complete = _pendingCompletionIdSet.Contains(achievementId)
                        ? DateTime.MinValue
                        : _completionTimes.GetValueOrDefault(achievementId)
                })
                .Where(entry => entry.Amount > 0 || entry.Complete > DateTime.MinValue)
                .OrderBy(entry => entry.Id)
                .ToList();
        }

        if (entries.Count == 0)
            return [new SCAchievementsPacket([])];

        return entries
            .Chunk(SCAchievementsPacket.MaxEntries)
            .Select(chunk => new SCAchievementsPacket(chunk.ToList()))
            .ToList();
    }

    public void SendSnapshot()
    {
        lock (_syncRoot)
        {
            if (_pendingCompletionIds.Count > 0 && CanPersistLive() && TryPersistState())
                ClearPendingCompletions();

            foreach (var packet in CreateSnapshotPackets())
                Owner.SendPacket(packet);
        }
    }

    public uint GetAmount(uint achievementId)
    {
        lock (_syncRoot)
            return _achievementAmounts.GetValueOrDefault(achievementId);
    }

    public bool IsCompleted(uint achievementId)
    {
        lock (_syncRoot)
            return _completionTimes.ContainsKey(achievementId);
    }

    public DateTime GetCompletionTime(uint achievementId)
    {
        lock (_syncRoot)
            return _completionTimes.GetValueOrDefault(achievementId);
    }

    private bool SetRecordMaximum(CharRecordKind kind, uint value1, uint value2, uint amount)
    {
        return SetRecordMaximum(kind, value1, value2, amount, out _);
    }

    private bool SetRecordMaximum(CharRecordKind kind, uint value1, uint value2, uint amount, out uint recordId)
    {
        recordId = 0;
        if (!_gameData.TryGetCharRecord(kind, value1, value2, out var record))
            return false;

        recordId = record.Id;
        if (_recordAmounts.GetValueOrDefault(record.Id) >= amount)
            return false;

        _recordAmounts[record.Id] = amount;
        return true;
    }

    private void Evaluate(IEnumerable<uint> initialAchievementIds, List<GamePacket> notifications)
    {
        var queue = new Queue<uint>();
        var queued = new HashSet<uint>();
        foreach (var achievementId in initialAchievementIds.Order())
            Enqueue(achievementId, queue, queued);

        while (queue.TryDequeue(out var achievementId))
        {
            queued.Remove(achievementId);
            if (!_gameData.TryGetAchievement(achievementId, out var achievement) || !achievement.IsActive)
                continue;

            var objectives = _gameData.GetObjectives(achievementId);
            if (objectives.Count == 0)
                continue;

            ulong total = 0;
            uint completedObjectives = 0;
            foreach (var objective in objectives)
            {
                var objectiveAmount = _recordAmounts.GetValueOrDefault(objective.RecordId);
                total += objectiveAmount;
                if (objectiveAmount > 0)
                    completedObjectives++;
            }

            var rawAmount = achievement.CompleteOr ? completedObjectives : Math.Min(total, uint.MaxValue);
            var amount = achievement.CompleteNum > 0
                ? (uint)Math.Min(rawAmount, achievement.CompleteNum)
                : completedObjectives;
            var wasCompleted = _completionTimes.ContainsKey(achievementId);
            if (wasCompleted)
            {
                var terminalAmount = achievement.CompleteNum > 0
                    ? achievement.CompleteNum
                    : (uint)objectives.Count;
                amount = Math.Max(amount, terminalAmount);
            }

            var oldAmount = _achievementAmounts.GetValueOrDefault(achievementId);
            _achievementAmounts[achievementId] = amount;

            if (notifications != null && !wasCompleted && oldAmount != amount)
                notifications.Add(new SCAchievementChangedPacket(achievementId, checked((int)amount)));

            var progressComplete = achievement.CompleteNum == 0
                ? completedObjectives == objectives.Count
                : rawAmount >= achievement.CompleteNum;
            var prerequisitesComplete = _gameData.GetPrerequisites(achievementId)
                .All(prerequisite => _completionTimes.ContainsKey(prerequisite.CompletedAchievementId));
            if (wasCompleted || !progressComplete || !prerequisitesComplete)
                continue;

            var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            _completionTimes.Add(achievementId, completedAt);
            if (_pendingCompletionIdSet.Add(achievementId))
                _pendingCompletionIds.Add(achievementId);

            if (SetRecordMaximum(CharRecordKind.CompleteAchievement, achievementId, 0, 1, out var recordId))
            {
                foreach (var linkedAchievementId in _gameData.GetAchievementIdsForRecord(recordId))
                    Enqueue(linkedAchievementId, queue, queued);
            }

            foreach (var unlockedAchievementId in _gameData.GetAchievementIdsUnlockedBy(achievementId))
                Enqueue(unlockedAchievementId, queue, queued);
        }
    }

    private static void Enqueue(uint achievementId, Queue<uint> queue, HashSet<uint> queued)
    {
        if (queued.Add(achievementId))
            queue.Enqueue(achievementId);
    }

    private static uint GetFullHours(TimeSpan onlineTime)
    {
        if (onlineTime <= TimeSpan.Zero)
            return 0;
        return (uint)Math.Min(Math.Floor(onlineTime.TotalHours), uint.MaxValue);
    }

    private bool CanPersistLive()
    {
        return Owner.Connection != null || _persistStateOverride != null;
    }

    private bool TryPersistState()
    {
        if (_persistStateOverride != null)
        {
            try
            {
                return _persistStateOverride();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to persist new achievements for character {CharacterId}", Owner.Id);
                return false;
            }
        }

        try
        {
            using var connection = MySQL.CreateConnection();
            return TryPersistState(connection);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to persist new achievements for character {CharacterId}", Owner.Id);
            return false;
        }
    }

    private bool TryPersistState(MySqlConnection connection)
    {
        MySqlTransaction transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            Save(connection, transaction);
            transaction.Commit();
            return true;
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                try
                {
                    transaction.Rollback();
                }
                catch (Exception rollbackException)
                {
                    Logger.Error(rollbackException,
                        "Failed to roll back achievement persistence for character {CharacterId}", Owner.Id);
                }
            }

            Logger.Error(ex, "Failed to persist new achievements for character {CharacterId}", Owner.Id);
            return false;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private void ClearPendingCompletions()
    {
        _pendingCompletionIds.Clear();
        _pendingCompletionIdSet.Clear();
    }

    private static void ExecuteTransactionCommand(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Connection = connection;
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}
