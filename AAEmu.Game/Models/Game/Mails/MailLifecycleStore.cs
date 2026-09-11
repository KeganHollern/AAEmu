using System.Text.Json;
using AAEmu.Game.Core.Managers;
using AAEmu.Commons.Utils.DB;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Mails;

internal enum MailTerminalOutcome : byte
{
    Returned = 1,
    Archived = 2,
    Removed = 3
}

/// <summary>Immutable source evidence, separate from the active mailbox and its item rows.</summary>
internal static class MailLifecycleStore
{
    // Connector/NET converts TINYINT(1) to Boolean before typed reader methods see it.
    // Project numeric grade separately without changing the connection's behavior for other consumers.
    internal const string ItemSnapshotColumns = "items.*, CAST(grade AS SIGNED) AS archive_numeric_grade";
    internal static bool? IsActiveSender(uint id)
    {
        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT deleted FROM characters WHERE id=@id";
            command.Parameters.AddWithValue("@id", id);
            var deleted = command.ExecuteScalar();
            return deleted != null && Convert.ToInt32(deleted) == 0;
        }
        catch (MySqlException)
        {
            return null; // Unavailable SQL is not evidence of a deleted sender.
        }
    }

    internal static string Snapshot(BaseMail mail) => JsonSerializer.Serialize(new
    {
        version = 1, id = mail.Id, type = (byte)mail.MailType, status = (byte)mail.Header.Status,
        title = mail.Title, text = mail.Body.Text,
        sender_id = mail.Header.SenderId, sender_name = mail.Header.SenderName,
        receiver_id = mail.Header.ReceiverId, receiver_name = mail.ReceiverName,
        attachment_count = mail.Header.Attachments, open_date = mail.OpenDate,
        send_date = mail.Body.SendDate, received_date = mail.Body.RecvDate,
        returned = mail.Header.Returned, extra = mail.Header.Extra,
        money_amount_1 = mail.Body.CopperCoins, money_amount_2 = mail.Body.BillingAmount,
        money_amount_3 = mail.Body.MoneyAmount2,
        attachments = mail.Body.Attachments.Select(item => item.Id).ToArray()
    });

    internal static void Write(PersistenceSaveContext context, BaseMail source,
        string snapshot, MailTerminalOutcome outcome, DateTime now, long returnedId, uint actorId,
        LegacyAuctionMailArchive auctionArchive = null)
    {
        auctionArchive?.ValidateAfterSave(context);
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        if (outcome == MailTerminalOutcome.Returned ||
            (outcome == MailTerminalOutcome.Archived && source.CanReturnMail()))
        {
            command.CommandText = "SELECT deleted FROM characters WHERE id=@sender FOR UPDATE";
            command.Parameters.AddWithValue("@sender", source.Header.SenderId);
            var deleted = command.ExecuteScalar();
            var active = deleted != null && Convert.ToInt32(deleted) == 0;
            if (active != (outcome == MailTerminalOutcome.Returned))
                throw new InvalidOperationException($"Mail {source.Id} sender state changed before commit.");
            command.Parameters.Clear();
        }
        command.CommandText = """
            INSERT INTO mail_lifecycle
                (mail_id, outcome, transitioned_at, returned_mail_id, actor_character_id, source_mail)
            VALUES (@id, @outcome, @now, @returned, @actor, @source)
            """;
        command.Parameters.AddWithValue("@id", source.Id);
        command.Parameters.AddWithValue("@outcome", (byte)outcome);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@returned", returnedId == 0 ? DBNull.Value : returnedId);
        command.Parameters.AddWithValue("@actor", actorId);
        command.Parameters.AddWithValue("@source", snapshot);
        command.ExecuteNonQuery();

        if (outcome != MailTerminalOutcome.Archived)
            return;
        foreach (var item in source.Body.Attachments)
        {
            command.Parameters.Clear();
            command.CommandText = $"SELECT {ItemSnapshotColumns} FROM items WHERE id=@id AND owner=@owner AND slot_type=@slot FOR UPDATE";
            command.Parameters.AddWithValue("@id", item.Id);
            command.Parameters.AddWithValue("@owner", source.Header.ReceiverId);
            command.Parameters.AddWithValue("@slot", (byte)(auctionArchive?.Contains(item.Id) == true ?
                Items.SlotType.Auction : Items.SlotType.Mail));
            Dictionary<string, object> row;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    throw new InvalidOperationException($"Mail {source.Id} has no persistent attachment {item.Id}.");
                row = ReadItemRow(reader);
            }
            var itemSnapshot = JsonSerializer.Serialize(row);
            auctionArchive?.CheckUnchanged(item.Id, itemSnapshot);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO mail_archive_items (item_id, mail_id, item_row) VALUES (@item, @mail, @row)";
            command.Parameters.AddWithValue("@item", item.Id);
            command.Parameters.AddWithValue("@mail", source.Id);
            command.Parameters.AddWithValue("@row", itemSnapshot);
            command.ExecuteNonQuery();
        }
    }

    internal static Dictionary<string, object> ReadItemRow(MySqlDataReader reader)
    {
        var row = new Dictionary<string, object>();
        // The final projected field replaces grade's representation, not the original row's schema.
        for (var index = 0; index < reader.FieldCount - 1; index++)
        {
            var name = reader.GetName(index);
            var valueIndex = name == "grade" ? reader.FieldCount - 1 : index;
            row.Add(name, reader.IsDBNull(valueIndex) ? null : reader.GetValue(valueIndex));
        }
        return row;
    }
}
