using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers;

internal static class CharacterDeletionStore
{
    internal static bool HasAuctionObligations(MySqlConnection connection, uint characterId, MySqlTransaction transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM auction_house WHERE client_id=@id OR bidder_id=@id LIMIT 1";
        command.Parameters.AddWithValue("@id", characterId);
        return command.ExecuteScalar() != null;
    }

    internal static void Delete(PersistenceSaveContext context, Character character, string deletedName)
    {
        // Item/mail/auction saves ran first in this checkpoint. Any obligation
        // that was still only in memory is now visible in this transaction.
        if (HasAuctionObligations(context.Connection, character.Id, context.Transaction))
            throw new InvalidOperationException("Character deletion still has auction obligations.");
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.Parameters.AddWithValue("@id", character.Id);
        command.Parameters.AddWithValue("@account", character.AccountId);
        command.Parameters.AddWithValue("@name", deletedName);
        command.Parameters.AddWithValue("@minimum", DateTime.MinValue);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@public", (byte)HousingPermission.Public);
        command.Parameters.AddWithValue("@expired", DateTime.UtcNow.AddDays(-21));
        command.CommandText = "UPDATE characters SET deleted=1,delete_time=@minimum,name=@name,money=0,money2=0," +
            "honor_point=0,vocation_point=0 WHERE id=@id AND account_id=@account AND deleted=0 " +
            "AND delete_time>@minimum AND delete_time<=@now";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Character deletion source changed before commit.");

        // House/coffer lifecycle stays with HousingManager. Shared coffer items
        // and immutable mail archive rows are not personal inventory to destroy.
        command.CommandText = "DELETE i FROM items i LEFT JOIN item_containers c ON c.container_id=i.container_id " +
            "LEFT JOIN mail_archive_items a ON a.item_id=i.id WHERE i.owner=@id AND a.item_id IS NULL " +
            "AND (c.container_type IS NULL OR c.container_type<>'CofferContainer')";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM item_containers WHERE owner_id=@id AND container_type<>'CofferContainer'";
        command.ExecuteNonQuery();
        foreach (var table in new[] { "mates", "abilities", "actabilities", "appellations", "options", "skills",
                     "quests", "completed_quests", "portal_book_coords", "portal_visited_district", "blocked", "friends" })
        {
            command.CommandText = $"DELETE FROM `{table}` WHERE owner=@id";
            command.ExecuteNonQuery();
        }
        foreach (var table in new[] { "character_active_buffs", "character_cooldowns", "character_cooldown_tags",
                     "character_achievement_records", "character_achievements" })
        {
            command.CommandText = $"DELETE FROM `{table}` WHERE character_id=@id";
            command.ExecuteNonQuery();
        }
        command.CommandText = "DELETE FROM slaves WHERE summoner=@id";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM mails WHERE receiver_id=@id";
        command.ExecuteNonQuery();
        // Commit the existing house-expiry rule with the tombstone so a restart
        // cannot leave a protected house owned by a deleted character.
        command.CommandText = "UPDATE housings SET permission=@public,protected_until=@expired WHERE owner=@id";
        command.ExecuteNonQuery();
    }
}
