using System.Collections.Concurrent;

using AAEmu.Commons.Utils.DB;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Models.Game.Units;

public class UnitCooldowns
{
    private const string SavepointName = "unit_cooldowns";

    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<uint, CooldownState> _cooldowns = new();

    private readonly record struct CooldownState(DateTime EndTime, uint Duration);

    public readonly record struct CooldownSnapshot(uint SkillId, uint Duration, uint Remaining);

    public int Count => _cooldowns.Count;

    public bool Contains(uint skillId)
    {
        return _cooldowns.ContainsKey(skillId);
    }

    public void AddCooldown(uint skillId, uint duration)
    {
        var state = new CooldownState(DateTime.UtcNow + TimeSpan.FromMilliseconds(duration), duration);
        _cooldowns.TryAdd(skillId, state);
    }

    public bool CheckCooldown(uint skillId)
    {
        if (!_cooldowns.TryGetValue(skillId, out var state))
            return false;

        var timeLeft = state.EndTime - DateTime.UtcNow;

        //Logger.Debug($"CheckCooldown: timeLeft={timeLeft}");

        if (timeLeft > TimeSpan.FromMilliseconds(250))
            return true;

        TryRemove(skillId, state);
        return false;
    }

    public void RemoveCooldown(uint skillId)
    {
        _cooldowns.TryRemove(skillId, out _);
    }

    /// <summary>
    /// Returns the active r208022 cooldown tuple: skill id, total duration, and remaining
    /// duration. All duration values use milliseconds on the wire.
    /// </summary>
    public IReadOnlyList<CooldownSnapshot> GetActiveSnapshots(int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);

        var utcNow = DateTime.UtcNow;
        var snapshots = new List<CooldownSnapshot>(Math.Min(_cooldowns.Count, maximumCount));
        foreach (var (skillId, state) in _cooldowns.OrderBy(entry => entry.Key))
        {
            if (snapshots.Count >= maximumCount)
                break;

            var remaining = state.EndTime - utcNow;
            if (remaining <= TimeSpan.Zero)
            {
                TryRemove(skillId, state);
                continue;
            }

            var remainingMilliseconds = ToWireMilliseconds(remaining);
            var totalDuration = Math.Max(state.Duration, remainingMilliseconds);
            snapshots.Add(new CooldownSnapshot(skillId, totalDuration, remainingMilliseconds));
        }

        return snapshots;
    }

    /// <summary>
    /// Persists still-active cooldowns for a character so they survive relogs.
    /// Called during Character.Save(). Expired entries are not written.
    /// </summary>
    public void Save(MySqlConnection connection, MySqlTransaction transaction, uint characterId)
    {
        var savepointCreated = false;
        try
        {
            ExecuteTransactionCommand(connection, transaction, $"SAVEPOINT `{SavepointName}`");
            savepointCreated = true;

            var utcNow = DateTime.UtcNow;
            var activeCooldowns = _cooldowns
                .Where(entry => entry.Value.EndTime > utcNow)
                .OrderBy(entry => entry.Key)
                .ToArray();

            // Upsert first. If the schema or an insert is invalid, existing cooldown rows remain.
            foreach (var (skillId, state) in activeCooldowns)
            {
                using var cmd = connection.CreateCommand();
                cmd.Connection = connection;
                cmd.Transaction = transaction;
                cmd.CommandText =
                    "INSERT INTO `character_cooldowns` (`character_id`, `skill_id`, `duration_ms`, `expires_at`) " +
                    "VALUES (@characterId, @skillId, @durationMs, @expiresAt) " +
                    "ON DUPLICATE KEY UPDATE `duration_ms`=@durationMs, `expires_at`=@expiresAt";
                cmd.Parameters.AddWithValue("@characterId", characterId);
                cmd.Parameters.AddWithValue("@skillId", skillId);
                cmd.Parameters.AddWithValue("@durationMs", state.Duration);
                cmd.Parameters.AddWithValue("@expiresAt", state.EndTime);
                cmd.ExecuteNonQuery();
            }

            // Remove rows that are no longer present only after every upsert succeeds.
            using var cleanupCmd = connection.CreateCommand();
            cleanupCmd.Connection = connection;
            cleanupCmd.Transaction = transaction;
            cleanupCmd.Parameters.AddWithValue("@characterId", characterId);
            if (activeCooldowns.Length == 0)
            {
                // The duration_ms reference makes an unmigrated schema fail before it deletes rows.
                cleanupCmd.CommandText =
                    "DELETE FROM `character_cooldowns` " +
                    "WHERE `character_id` = @characterId AND `duration_ms` IS NOT NULL";
            }
            else
            {
                cleanupCmd.CommandText =
                    "DELETE FROM `character_cooldowns` WHERE `character_id` = @characterId AND `skill_id` NOT IN (" +
                    string.Join(",", activeCooldowns.Select((_, i) => $"@active{i}")) + ")";
                for (var i = 0; i < activeCooldowns.Length; i++)
                    cleanupCmd.Parameters.AddWithValue($"@active{i}", activeCooldowns[i].Key);
            }
            cleanupCmd.ExecuteNonQuery();

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

    /// <summary>
    /// Restores persisted cooldowns when a character enters the world.
    /// Expired rows are removed. Server-side enforcement via CheckCooldown()
    /// and client-side display through SCCooldownsPacket use the restored state.
    /// </summary>
    public void Load(uint characterId)
    {
        try
        {
            var restoredCount = 0;
            var expiredIds = new List<uint>();
            var utcNow = DateTime.UtcNow;

            using var connection = MySQL.CreateConnection();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM `character_cooldowns` WHERE `character_id` = @characterId";
                cmd.Parameters.AddWithValue("@characterId", characterId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var skillId = reader.GetUInt32("skill_id");
                    var duration = reader.GetUInt32("duration_ms");
                    var expiresAt = reader.GetDateTime("expires_at");

                    if (expiresAt <= utcNow)
                    {
                        expiredIds.Add(skillId);
                        continue;
                    }

                    var remaining = ToWireMilliseconds(expiresAt - utcNow);
                    _cooldowns[skillId] = new CooldownState(expiresAt, Math.Max(duration, remaining));
                    restoredCount++;
                }
            }

            if (expiredIds.Count > 0)
            {
                using var cleanupCmd = connection.CreateCommand();
                cleanupCmd.CommandText =
                    "DELETE FROM `character_cooldowns` WHERE `character_id` = @characterId AND `skill_id` IN (" +
                    string.Join(",", expiredIds.Select((_, i) => $"@expired{i}")) + ")";
                cleanupCmd.Parameters.AddWithValue("@characterId", characterId);
                for (var i = 0; i < expiredIds.Count; i++)
                    cleanupCmd.Parameters.AddWithValue($"@expired{i}", expiredIds[i]);
                cleanupCmd.ExecuteNonQuery();
            }

            if (restoredCount > 0)
                Logger.Debug("Restored {Count} cooldown(s) for character {CharacterId}", restoredCount, characterId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load cooldowns for character {CharacterId}", characterId);
        }
    }

    private static uint ToWireMilliseconds(TimeSpan duration)
    {
        var milliseconds = Math.Ceiling(duration.TotalMilliseconds);
        if (milliseconds <= 0)
            return 0;
        if (milliseconds >= uint.MaxValue)
            return uint.MaxValue;
        return (uint)milliseconds;
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

    private bool TryRemove(uint skillId, CooldownState expectedState)
    {
        return ((ICollection<KeyValuePair<uint, CooldownState>>)_cooldowns)
            .Remove(new KeyValuePair<uint, CooldownState>(skillId, expectedState));
    }
}
