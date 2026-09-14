using System.Collections.Concurrent;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills.Templates;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Models.Game.Units;

public class UnitCooldowns
{
    private const string SavepointName = "unit_cooldowns";
    internal const int NetworkToleranceMilliseconds = 50;

    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<uint, CooldownState> _cooldowns = new();
    private readonly ConcurrentDictionary<uint, CooldownState> _tags = new();
    private readonly TimeProvider _timeProvider;

    private readonly record struct CooldownState(DateTime EndTime, uint Duration);
    public readonly record struct CooldownSnapshot(uint Id, uint Duration, uint Remaining);
    public readonly record struct CooldownBuckets(
        IReadOnlyList<CooldownSnapshot> Skills, IReadOnlyList<CooldownSnapshot> Tags);

    public UnitCooldowns(TimeProvider timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count => _cooldowns.Count;

    public bool Contains(uint skillId)
    {
        return _cooldowns.ContainsKey(skillId);
    }

    public void AddCooldown(uint skillId, uint duration, int cooldownTagId = 0)
    {
        if (duration == 0)
            return;
        var state = new CooldownState(UtcNow.AddMilliseconds(duration), duration);
        if (cooldownTagId > 0)
            AddOrExtend(_tags, (uint)cooldownTagId, state);
        else
            AddOrExtend(_cooldowns, skillId, state);
    }

    public bool CheckCooldown(uint skillId)
    {
        if (CheckCooldown(_cooldowns, skillId, UtcNow))
            return true;
        if (_tags.IsEmpty)
            return false;

        // AI callers pass only an ID and still need the authored shared bucket checks.
        var skill = SkillManager.Instance.GetSkillTemplate(skillId);
        return skill != null && CheckCooldown(skill);
    }

    public bool CheckCooldown(SkillTemplate skill)
    {
        var utcNow = UtcNow;
        if (CheckCooldown(_cooldowns, skill.Id, utcNow) ||
            (skill.CooldownTagId > 0 && CheckCooldown(_tags, (uint)skill.CooldownTagId, utcNow)))
            return true;

        // The native getter checks every authored skill tag, not only cooldown_tag_id.
        return SkillManager.Instance.GetSkillTags(skill.Id).Any(tag => CheckCooldown(_tags, tag, utcNow));
    }

    public bool CheckTagCooldown(uint tagId)
    {
        return CheckCooldown(_tags, tagId, UtcNow);
    }

    public void RemoveCooldown(uint skillId)
    {
        _cooldowns.TryRemove(skillId, out _);
    }

    public void RemoveTagCooldown(uint tagId)
    {
        _tags.TryRemove(tagId, out _);
    }

    public void RemoveCooldown(SkillTemplate skill)
    {
        RemoveCooldown(skill.Id);
        if (skill.CooldownTagId > 0)
            RemoveTagCooldown((uint)skill.CooldownTagId);
    }

    public IReadOnlyList<CooldownSnapshot> GetActiveSnapshots(int maximumCount)
    {
        return GetActiveSnapshots(_cooldowns, maximumCount, UtcNow);
    }

    public CooldownBuckets GetActiveBuckets(int maximumCount)
    {
        var utcNow = UtcNow;
        return new CooldownBuckets(GetActiveSnapshots(_cooldowns, maximumCount, utcNow),
            GetActiveSnapshots(_tags, maximumCount, utcNow));
    }

    private static List<CooldownSnapshot> GetActiveSnapshots(
        ConcurrentDictionary<uint, CooldownState> bucket, int maximumCount, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        var snapshots = new List<CooldownSnapshot>(Math.Min(bucket.Count, maximumCount));
        foreach (var (id, state) in bucket.OrderBy(entry => entry.Key))
        {
            if (snapshots.Count >= maximumCount)
                break;
            var remaining = state.EndTime - utcNow;
            if (remaining <= TimeSpan.Zero)
            {
                TryRemove(bucket, id, state);
                continue;
            }
            var milliseconds = ToWireMilliseconds(remaining);
            snapshots.Add(new CooldownSnapshot(id, Math.Max(state.Duration, milliseconds), milliseconds));
        }
        return snapshots;
    }

    /// <summary>Persists both native buckets in the character transaction.</summary>
    public void Save(MySqlConnection connection, MySqlTransaction transaction, uint characterId)
    {
        var savepointCreated = false;
        try
        {
            ExecuteTransactionCommand(connection, transaction, $"SAVEPOINT `{SavepointName}`");
            savepointCreated = true;
            var utcNow = UtcNow;
            SaveBucket(connection, transaction, characterId, "character_cooldowns", "skill_id", _cooldowns, utcNow);
            SaveBucket(connection, transaction, characterId, "character_cooldown_tags", "tag_id", _tags, utcNow);
            ExecuteTransactionCommand(connection, transaction, $"RELEASE SAVEPOINT `{SavepointName}`");
            savepointCreated = false;
        }
        catch (Exception ex)
        {
            if (savepointCreated)
            {
                try
                {
                    ExecuteTransactionCommand(connection, transaction, $"ROLLBACK TO SAVEPOINT `{SavepointName}`");
                    ExecuteTransactionCommand(connection, transaction, $"RELEASE SAVEPOINT `{SavepointName}`");
                }
                catch (Exception rollbackException)
                {
                    Logger.Error(rollbackException,
                        "Failed to roll back cooldown savepoint for character {CharacterId}", characterId);
                }
            }
            Logger.Error(ex, "Failed to save cooldowns for character {CharacterId}", characterId);
        }
    }

    private static void SaveBucket(MySqlConnection connection, MySqlTransaction transaction, uint characterId,
        string table, string idColumn, ConcurrentDictionary<uint, CooldownState> bucket, DateTime utcNow)
    {
        var active = bucket.Where(entry => entry.Value.EndTime > utcNow).OrderBy(entry => entry.Key).ToArray();
        foreach (var (id, state) in active)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"INSERT INTO `{table}` (`character_id`, `{idColumn}`, `duration_ms`, `expires_at`) " +
                "VALUES (@characterId, @id, @durationMs, @expiresAt) " +
                "ON DUPLICATE KEY UPDATE `duration_ms`=@durationMs, `expires_at`=@expiresAt";
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@durationMs", state.Duration);
            command.Parameters.AddWithValue("@expiresAt", state.EndTime);
            command.ExecuteNonQuery();
        }

        using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.Parameters.AddWithValue("@characterId", characterId);
        // The duration column also makes an unmigrated schema fail before deleting rows.
        cleanup.CommandText = $"DELETE FROM `{table}` WHERE `character_id`=@characterId AND `duration_ms` IS NOT NULL";
        if (active.Length > 0)
        {
            cleanup.CommandText += $" AND `{idColumn}` NOT IN (" +
                string.Join(",", active.Select((_, i) => $"@active{i}")) + ")";
            for (var i = 0; i < active.Length; i++)
                cleanup.Parameters.AddWithValue($"@active{i}", active[i].Key);
        }
        cleanup.ExecuteNonQuery();
    }

    /// <summary>Restores exact duration and expiry; old tagged skill rows become shared timers.</summary>
    public void Load(uint characterId)
    {
        try
        {
            var utcNow = UtcNow;
            using var connection = MySQL.CreateConnection();
            LoadBucket(connection, characterId, "character_cooldowns", "skill_id", _cooldowns, utcNow, true);
            LoadBucket(connection, characterId, "character_cooldown_tags", "tag_id", _tags, utcNow, false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load cooldowns for character {CharacterId}", characterId);
        }
    }

    private void LoadBucket(MySqlConnection connection, uint characterId, string table, string idColumn,
        ConcurrentDictionary<uint, CooldownState> bucket, DateTime utcNow, bool legacySkills)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT `{idColumn}`, `duration_ms`, `expires_at` FROM `{table}` WHERE `character_id`=@characterId";
            command.Parameters.AddWithValue("@characterId", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetUInt32(idColumn);
                var expiresAt = reader.GetDateTime("expires_at");
                if (expiresAt <= utcNow)
                    continue;
                var duration = Math.Max(reader.GetUInt32("duration_ms"), ToWireMilliseconds(expiresAt - utcNow));
                var state = new CooldownState(expiresAt, duration);
                var tag = legacySkills ? SkillManager.Instance.GetSkillTemplate(id)?.CooldownTagId ?? 0 : 0;
                if (tag > 0)
                    AddOrExtend(_tags, (uint)tag, state);
                else
                    AddOrExtend(bucket, id, state);
            }
        }
        using var cleanup = connection.CreateCommand();
        cleanup.CommandText = $"DELETE FROM `{table}` WHERE `character_id`=@characterId AND `expires_at`<=@utcNow";
        cleanup.Parameters.AddWithValue("@characterId", characterId);
        cleanup.Parameters.AddWithValue("@utcNow", utcNow);
        cleanup.ExecuteNonQuery();
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool CheckCooldown(ConcurrentDictionary<uint, CooldownState> bucket, uint id, DateTime utcNow)
    {
        if (!bucket.TryGetValue(id, out var state))
            return false;
        if (state.EndTime - utcNow > TimeSpan.FromMilliseconds(Math.Min(NetworkToleranceMilliseconds, state.Duration / 20.0)))
            return true;
        TryRemove(bucket, id, state);
        return false;
    }

    private static void AddOrExtend(ConcurrentDictionary<uint, CooldownState> bucket, uint id, CooldownState state)
    {
        // r208022 keeps the longer remaining timer when a skill or tag is already active.
        bucket.AddOrUpdate(id, state, (_, current) =>
            current.EndTime > state.EndTime ||
            (current.EndTime == state.EndTime && current.Duration >= state.Duration) ? current : state);
    }

    private static uint ToWireMilliseconds(TimeSpan duration)
    {
        return (uint)Math.Clamp(Math.Ceiling(duration.TotalMilliseconds), 0, uint.MaxValue);
    }

    private static void ExecuteTransactionCommand(MySqlConnection connection, MySqlTransaction transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static bool TryRemove(ConcurrentDictionary<uint, CooldownState> bucket, uint id, CooldownState expectedState)
    {
        return ((ICollection<KeyValuePair<uint, CooldownState>>)bucket)
            .Remove(new KeyValuePair<uint, CooldownState>(id, expectedState));
    }
}
