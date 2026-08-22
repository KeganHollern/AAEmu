using System.Collections.Concurrent;

using AAEmu.Commons.Utils.DB;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Models.Game.Units;

public class UnitCooldowns
{
    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public ConcurrentDictionary<uint, DateTime> Cooldowns { get; set; } = new();

    public void AddCooldown(uint skillId, uint duration)
    {
        if (!Cooldowns.TryGetValue(skillId, out _))
            Cooldowns.TryAdd(skillId, DateTime.UtcNow + TimeSpan.FromMilliseconds(duration));
    }

    public bool CheckCooldown(uint skillId)
    {
        if (!Cooldowns.TryGetValue(skillId, out var endTime))
            return false;

        var timeLeft = endTime - DateTime.UtcNow;

        //Logger.Debug($"CheckCooldown: timeLeft={timeLeft}");

        if (timeLeft > TimeSpan.FromMilliseconds(250))
            return true;

        RemoveCooldown(skillId);
        return false;
    }

    public void RemoveCooldown(uint skillId)
    {
        Cooldowns.TryRemove(skillId, out _);
    }

    /// <summary>
    /// Persists still-active cooldowns for a character so they survive relogs.
    /// Called during Character.Save(). Expired entries are not written.
    /// </summary>
    public void Save(MySqlConnection connection, MySqlTransaction transaction, uint characterId)
    {
        try
        {
            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Connection = connection;
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM `character_cooldowns` WHERE `character_id` = @characterId";
                deleteCmd.Parameters.AddWithValue("@characterId", characterId);
                deleteCmd.ExecuteNonQuery();
            }

            var utcNow = DateTime.UtcNow;
            foreach (var (skillId, endTime) in Cooldowns)
            {
                if (endTime <= utcNow)
                    continue; // Already expired; nothing to persist

                using var cmd = connection.CreateCommand();
                cmd.Connection = connection;
                cmd.Transaction = transaction;
                cmd.CommandText =
                    "INSERT INTO `character_cooldowns` (`character_id`, `skill_id`, `expires_at`) " +
                    "VALUES (@characterId, @skillId, @expiresAt)";
                cmd.Parameters.AddWithValue("@characterId", characterId);
                cmd.Parameters.AddWithValue("@skillId", skillId);
                cmd.Parameters.AddWithValue("@expiresAt", endTime);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save cooldowns for character {CharacterId}", characterId);
        }
    }

    /// <summary>
    /// Restores persisted cooldowns when a character enters the world.
    /// Expired rows are removed. Server-side enforcement via CheckCooldown()
    /// applies immediately; the client-side cooldown display packet has an
    /// unknown 1.2 wire format and is intentionally left empty.
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
                    var expiresAt = reader.GetDateTime("expires_at");

                    if (expiresAt <= utcNow)
                    {
                        expiredIds.Add(skillId);
                        continue;
                    }

                    Cooldowns[skillId] = expiresAt;
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
}
