using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.StaticValues;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Models.Game.Char;

public readonly record struct AchievementProgressEvent(
    CharRecordKind Kind,
    uint Value1,
    uint Value2,
    uint Amount,
    bool MatchValue2Wildcard = false);

public class CharacterAchievements
{
    private const string SavepointName = "character_achievements";

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly object _syncRoot = new();
    private readonly AchievementGameData _gameData;
    private readonly UnitRequirementsGameData _unitRequirementsData;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _persistStateOverride;
    private readonly Func<uint, uint, AchievementRewardStatus?> _rewardDeliveryOverride;
    private readonly Func<Character, FactionsEnum?> _templateMotherFactionResolver;
    private readonly Dictionary<uint, uint> _recordAmounts = [];
    private readonly Dictionary<uint, uint> _achievementAmounts = [];
    private readonly Dictionary<uint, DateTime> _completionTimes = [];
    private readonly Dictionary<uint, AchievementRewardStatus> _rewardStatuses = [];
    private readonly List<uint> _pendingCompletionIds = [];
    private readonly HashSet<uint> _pendingCompletionIdSet = [];
    private readonly HashSet<uint> _pendingRewardIds = [];
    private bool _deferredPersistenceActive;
    private bool _rewardDeliveryInProgress;

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
        Func<bool> persistStateOverride,
        Func<uint, uint, AchievementRewardStatus?> rewardDeliveryOverride = null,
        UnitRequirementsGameData unitRequirementsData = null,
        Func<Character, FactionsEnum?> templateMotherFactionResolver = null)
    {
        Owner = owner;
        _gameData = gameData ?? AchievementGameData.Instance;
        _unitRequirementsData = unitRequirementsData ?? UnitRequirementsGameData.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persistStateOverride = persistStateOverride;
        _rewardDeliveryOverride = rewardDeliveryOverride;
        _templateMotherFactionResolver = templateMotherFactionResolver ?? GetTemplateMotherFaction;
    }

    public void Load(MySqlConnection connection)
    {
        lock (_syncRoot)
        {
            _recordAmounts.Clear();
            _achievementAmounts.Clear();
            _completionTimes.Clear();
            _rewardStatuses.Clear();
            _pendingCompletionIds.Clear();
            _pendingCompletionIdSet.Clear();
            _pendingRewardIds.Clear();

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
                    "SELECT `achievement_id`, `completed_at`, `reward_status` " +
                    "FROM `character_achievements` WHERE `character_id` = @character_id";
                command.Parameters.AddWithValue("@character_id", Owner.Id);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var completedAt = DateTime.SpecifyKind(reader.GetDateTime("completed_at"), DateTimeKind.Utc);
                        var achievementId = reader.GetUInt32("achievement_id");
                        _completionTimes[achievementId] = completedAt;
                        var rewardStatus = (AchievementRewardStatus)reader.GetByte("reward_status");
                        _rewardStatuses[achievementId] = rewardStatus;
                        if (rewardStatus == AchievementRewardStatus.Pending &&
                            _gameData.TryGetAchievement(achievementId, out var achievement) &&
                            achievement.IsActive &&
                            achievement.ItemId > 0)
                        {
                            _pendingRewardIds.Add(achievementId);
                        }
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
        var canDeliverRewards = true;
        lock (_syncRoot)
        {
            foreach (var achievementId in _completionTimes.Keys.ToArray())
                SetRecordMaximum(CharRecordKind.CompleteAchievement, achievementId, 0, 1);

            SetRecordMaximum(CharRecordKind.CharLevel, 0, 0, Owner.Level);
            SetRecordMaximum(CharRecordKind.PlayTime, 0, 0, GetFullHours(Owner.OnlineTime));
            var gold = Owner.Money <= 0
                ? 0u
                : (uint)Math.Min(Owner.Money / 10000L, uint.MaxValue);
            SetRecordMaximum(CharRecordKind.MyGold, 0, 0, gold);
            SetRecordMaximum(
                CharRecordKind.GetLifePoint,
                0,
                0,
                (uint)Math.Max(Owner.VocationPoint, 0));
            SetRecordMaximum(
                CharRecordKind.SpendLabor,
                0,
                0,
                (uint)Math.Max(Owner.ConsumedLaborPower, 0));
            SetRecordMaximum(
                CharRecordKind.GetJuryPoint,
                0,
                0,
                (uint)Math.Max(Owner.JuryPoint - 1, 0));
            SetRecordMaximum(
                CharRecordKind.Judgement,
                0,
                0,
                (uint)Math.Max(Owner.NotGuiltyCount, 0));
            SetRecordMaximum(
                CharRecordKind.Judgement,
                1,
                0,
                (uint)Math.Max(Owner.GuiltyCount, 0));

            if (Owner.Actability != null)
            {
                foreach (var (actabilityId, actability) in Owner.Actability.Actabilities)
                {
                    SetRecordMaximum(
                        CharRecordKind.GetActability,
                        actabilityId,
                        0,
                        (uint)Math.Max(actability.Point, 0));
                }
            }

            if (Owner.Faction != null)
                SetRecordMaximum(CharRecordKind.GetFaction, (uint)Owner.Faction.Id, 0, 1);

            if (Owner.Mates != null && Owner.Inventory != null)
            {
                foreach (var mate in Owner.Mates.GetMateInfos())
                {
                    var item = Owner.Inventory.GetItemById(mate.ItemId);
                    if (item == null ||
                        ItemManager.Instance.GetTemplate(item.TemplateId) is not SummonMateTemplate mateTemplate)
                    {
                        continue;
                    }

                    SetRecordMaximum(CharRecordKind.PetLevel, mateTemplate.NpcId, 0, mate.Level);
                }
            }

            if (Owner.Quests != null)
            {
                Dictionary<uint, uint> completedQuestCountsByCategory = [];
                foreach (var questId in Owner.Quests.GetCompletedQuestIds())
                {
                    SetRecordMaximum(CharRecordKind.CompleteQuestType, questId, 0, 1);
                    var questTemplate = QuestManager.Instance.GetTemplate(questId);
                    if (questTemplate == null)
                        continue;

                    completedQuestCountsByCategory[questTemplate.CategoryId] =
                        completedQuestCountsByCategory.GetValueOrDefault(questTemplate.CategoryId) + 1;
                }

                foreach (var (categoryId, count) in completedQuestCountsByCategory)
                    SetRecordMaximum(CharRecordKind.CompleteQuestCategory, categoryId, 0, count);
            }

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
                else
                    canDeliverRewards = false;
            }
        }

        if (canDeliverRewards && (Owner.Connection != null || _rewardDeliveryOverride != null))
            TryDeliverPendingRewards();
    }

    internal DeferredPersistenceScope BeginDeferredPersistence()
    {
        Monitor.Enter(_syncRoot);
        try
        {
            if (_deferredPersistenceActive)
                throw new InvalidOperationException("Achievement persistence is already deferred.");

            _deferredPersistenceActive = true;
            return new DeferredPersistenceScope(this, CaptureState());
        }
        catch
        {
            Monitor.Exit(_syncRoot);
            throw;
        }
    }

    internal void SendCommittedState()
    {
        foreach (var packet in CreateSnapshotPackets())
            Owner.SendPacket(packet);

        TryDeliverPendingRewards();
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
        var canDeliverRewards = true;
        lock (_syncRoot)
        {
            var recordChanged = SetRecordMaximum(kind, value1, value2, amount, out var recordId);
            if (recordChanged)
                Evaluate(_gameData.GetAchievementIdsForRecord(recordId), notifications);

            if (_deferredPersistenceActive)
                return;

            if (_pendingCompletionIds.Count > 0)
            {
                canDeliverRewards = false;
                if (CanPersistLive() && TryPersistState())
                {
                    foreach (var achievementId in _pendingCompletionIds)
                    {
                        notifications.Add(new SCAchievementCompletedPacket(
                            achievementId,
                            _completionTimes[achievementId]));
                    }

                    ClearPendingCompletions();
                    canDeliverRewards = true;
                }
            }

            foreach (var notification in notifications)
                Owner.SendPacket(notification);
        }

        if (canDeliverRewards)
            TryDeliverPendingRewards();
    }

    public void Increment(
        CharRecordKind kind,
        uint value1,
        uint value2,
        uint amount = 1,
        bool matchValue2Wildcard = false)
    {
        Increment([new AchievementProgressEvent(kind, value1, value2, amount, matchValue2Wildcard)]);
    }

    public void Increment(IReadOnlyList<AchievementProgressEvent> progressEvents)
    {
        ArgumentNullException.ThrowIfNull(progressEvents);

        List<GamePacket> notifications = [];
        var canDeliverRewards = true;
        lock (_syncRoot)
        {
            HashSet<uint> affectedAchievementIds = [];
            foreach (var progressEvent in progressEvents)
            {
                if (progressEvent.Amount == 0)
                    continue;

                var records = _gameData.GetMatchingCharRecords(
                    progressEvent.Kind,
                    progressEvent.Value1,
                    progressEvent.Value2,
                    progressEvent.MatchValue2Wildcard);
                foreach (var record in records)
                {
                    if (!IncrementRecord(record, progressEvent.Amount))
                        continue;

                    foreach (var achievementId in _gameData.GetAchievementIdsForRecord(record.Id))
                        affectedAchievementIds.Add(achievementId);
                }
            }

            if (affectedAchievementIds.Count > 0)
                Evaluate(affectedAchievementIds, notifications);

            if (_deferredPersistenceActive)
                return;

            if (_pendingCompletionIds.Count > 0)
            {
                canDeliverRewards = false;
                if (CanPersistLive() && TryPersistState())
                {
                    foreach (var achievementId in _pendingCompletionIds)
                    {
                        notifications.Add(new SCAchievementCompletedPacket(
                            achievementId,
                            _completionTimes[achievementId]));
                    }

                    ClearPendingCompletions();
                    canDeliverRewards = true;
                }
            }

            foreach (var notification in notifications)
                Owner.SendPacket(notification);
        }

        if (canDeliverRewards)
            TryDeliverPendingRewards();
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
        var canDeliverRewards = false;
        lock (_syncRoot)
        {
            if (_pendingCompletionIds.Count > 0 && CanPersistLive() && TryPersistState())
            {
                ClearPendingCompletions();
                canDeliverRewards = true;
            }
            else if (_pendingCompletionIds.Count == 0)
                canDeliverRewards = true;

            foreach (var packet in CreateSnapshotPackets())
                Owner.SendPacket(packet);
        }

        if (canDeliverRewards)
            TryDeliverPendingRewards();
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

    public AchievementRewardStatus GetRewardStatus(uint achievementId)
    {
        lock (_syncRoot)
            return _rewardStatuses.GetValueOrDefault(achievementId);
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

    private bool IncrementRecord(CharRecords record, uint amount)
    {
        var oldAmount = _recordAmounts.GetValueOrDefault(record.Id);
        var newAmount = uint.MaxValue - oldAmount < amount
            ? uint.MaxValue
            : oldAmount + amount;
        if (newAmount == oldAmount)
            return false;

        _recordAmounts[record.Id] = newAmount;
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
                if (!MeetsObjectiveRequirements(objective))
                    continue;

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
                notifications.Add(new SCAchievementChangedPacket(
                    achievementId,
                    (int)Math.Min(amount, (uint)int.MaxValue)));

            var progressComplete = achievement.CompleteNum == 0
                ? completedObjectives == objectives.Count
                : rawAmount >= achievement.CompleteNum;
            var prerequisitesComplete = _gameData.GetPrerequisites(achievementId)
                .All(prerequisite => _completionTimes.ContainsKey(prerequisite.CompletedAchievementId));
            if (wasCompleted || !progressComplete || !prerequisitesComplete)
                continue;

            var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            _completionTimes.Add(achievementId, completedAt);
            if (achievement.ItemId > 0)
                _pendingRewardIds.Add(achievementId);
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

    private bool MeetsObjectiveRequirements(AchievementObjectives objective)
    {
        var requirements = _unitRequirementsData.GetAchievementObjectiveRequirements(objective.Id);
        if (requirements.Count == 0)
            return true;

        bool RequirementMatches(UnitReqs requirement)
        {
            if (requirement.KindType == UnitReqsKindType.MotherFaction)
            {
                var motherFaction = _templateMotherFactionResolver(Owner);
                return motherFaction.HasValue && (uint)motherFaction.Value == requirement.Value1;
            }

            return requirement.Validate(Owner, Owner).ResultKey == SkillResultKeys.ok;
        }

        return objective.OrUnitReqs
            ? requirements.Any(RequirementMatches)
            : requirements.All(RequirementMatches);
    }

    private static FactionsEnum? GetTemplateMotherFaction(Character character)
    {
        var template = CharacterManager.Instance.GetTemplate(character.Race, character.Gender);
        return FactionManager.Instance.GetFaction(template.FactionId)?.MotherId;
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

    private StateSnapshot CaptureState()
    {
        return new StateSnapshot(
            new Dictionary<uint, uint>(_recordAmounts),
            new Dictionary<uint, uint>(_achievementAmounts),
            new Dictionary<uint, DateTime>(_completionTimes),
            new Dictionary<uint, AchievementRewardStatus>(_rewardStatuses),
            [.. _pendingCompletionIds],
            [.. _pendingCompletionIdSet],
            [.. _pendingRewardIds]);
    }

    private void RestoreState(StateSnapshot snapshot)
    {
        _recordAmounts.Clear();
        foreach (var (recordId, amount) in snapshot.RecordAmounts)
            _recordAmounts[recordId] = amount;

        _achievementAmounts.Clear();
        foreach (var (achievementId, amount) in snapshot.AchievementAmounts)
            _achievementAmounts[achievementId] = amount;

        _completionTimes.Clear();
        foreach (var (achievementId, completedAt) in snapshot.CompletionTimes)
            _completionTimes[achievementId] = completedAt;

        _rewardStatuses.Clear();
        foreach (var (achievementId, rewardStatus) in snapshot.RewardStatuses)
            _rewardStatuses[achievementId] = rewardStatus;

        _pendingCompletionIds.Clear();
        _pendingCompletionIds.AddRange(snapshot.PendingCompletionIds);
        _pendingCompletionIdSet.Clear();
        _pendingCompletionIdSet.UnionWith(snapshot.PendingCompletionIdSet);
        _pendingRewardIds.Clear();
        _pendingRewardIds.UnionWith(snapshot.PendingRewardIds);
    }

    private void TryDeliverPendingRewards()
    {
        lock (_syncRoot)
        {
            if (_rewardDeliveryInProgress || _pendingRewardIds.Count == 0)
                return;
            _rewardDeliveryInProgress = true;
        }

        HashSet<uint> attemptedRewardIds = [];
        var continueDelivery = false;
        try
        {
            while (true)
            {
                uint[] pendingRewardIds;
                lock (_syncRoot)
                {
                    pendingRewardIds = _pendingRewardIds
                        .Where(achievementId => !attemptedRewardIds.Contains(achievementId))
                        .Order()
                        .ToArray();
                }
                if (pendingRewardIds.Length == 0)
                    break;

                foreach (var achievementId in pendingRewardIds)
                {
                    attemptedRewardIds.Add(achievementId);
                    if (!_gameData.TryGetAchievement(achievementId, out var achievement))
                    {
                        lock (_syncRoot)
                            _pendingRewardIds.Remove(achievementId);
                        continue;
                    }

                    AchievementRewardStatus? status;
                    try
                    {
                        if (_rewardDeliveryOverride != null)
                            status = _rewardDeliveryOverride(achievementId, achievement.ItemId);
                        else
                            status = AchievementRewardManager.TryDeliver(Owner, achievementId, achievement.ItemId);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(
                            ex,
                            "Failed to deliver achievement reward {AchievementId} to character {CharacterId}",
                            achievementId,
                            Owner.Id);
                        continue;
                    }

                    if (status is AchievementRewardStatus.Inventory or AchievementRewardStatus.Mail)
                    {
                        lock (_syncRoot)
                        {
                            _rewardStatuses[achievementId] = status.Value;
                            _pendingRewardIds.Remove(achievementId);
                        }
                    }
                }
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _rewardDeliveryInProgress = false;
                continueDelivery = _pendingRewardIds.Any(
                    achievementId => !attemptedRewardIds.Contains(achievementId));
            }
        }

        if (continueDelivery)
            TryDeliverPendingRewards();
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

    internal sealed class DeferredPersistenceScope : IDisposable
    {
        private readonly CharacterAchievements _owner;
        private readonly StateSnapshot _snapshot;
        private bool _finished;

        internal DeferredPersistenceScope(CharacterAchievements owner, StateSnapshot snapshot)
        {
            _owner = owner;
            _snapshot = snapshot;
        }

        public void Commit()
        {
            if (_finished)
                throw new InvalidOperationException("The deferred achievement update is already finished.");

            _owner.ClearPendingCompletions();
            Finish();
        }

        public void Dispose()
        {
            if (_finished)
                return;

            _owner.RestoreState(_snapshot);
            Finish();
        }

        private void Finish()
        {
            _owner._deferredPersistenceActive = false;
            _finished = true;
            Monitor.Exit(_owner._syncRoot);
        }
    }

    internal sealed record StateSnapshot(
        Dictionary<uint, uint> RecordAmounts,
        Dictionary<uint, uint> AchievementAmounts,
        Dictionary<uint, DateTime> CompletionTimes,
        Dictionary<uint, AchievementRewardStatus> RewardStatuses,
        List<uint> PendingCompletionIds,
        HashSet<uint> PendingCompletionIdSet,
        HashSet<uint> PendingRewardIds);
}
