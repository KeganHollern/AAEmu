using System.Text.Json;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Mails;

/// <summary>Locks the original, unclaimed winner-mail provenance before the economy checkpoint writes anything.</summary>
internal sealed class LegacyAuctionMailArchive(BaseMail source, IReadOnlyCollection<Item> attachments, DateTime now)
{
    private readonly Dictionary<ulong, string> _itemRows = [];

    internal bool Contains(ulong id) => _itemRows.ContainsKey(id);

    internal void CheckUnchanged(ulong id, string row)
    {
        if (_itemRows.TryGetValue(id, out var original) && original != row)
            throw new InvalidOperationException($"Legacy auction attachment {id} changed during the archive checkpoint.");
    }

    internal void ValidateSource(PersistenceSaveContext context)
    {
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = "SELECT * FROM mails WHERE id=@mail FOR UPDATE";
        command.Parameters.AddWithValue("@mail", source.Id);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt32("type") != (int)MailType.AucBidWin ||
                reader.GetUInt32("receiver_id") != source.Header.ReceiverId ||
                reader.GetDateTime("received_date") > now - MailManager.MailExpireDelay)
                throw new InvalidOperationException($"Mail {source.Id} is not a persisted expired auction winner mail.");
            var ids = Enumerable.Range(0, MailBody.MaxMailAttachments)
                .Select(index => reader.GetUInt64("attachment" + index)).Where(id => id != 0).ToArray();
            if (!ids.SequenceEqual(source.Body.Attachments.Select(item => item.Id)))
                throw new InvalidOperationException($"Mail {source.Id} attachment references changed before archive.");
        }
        foreach (var item in attachments)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@item", item.Id);
            command.CommandText = $"SELECT {MailLifecycleStore.ItemSnapshotColumns} FROM items WHERE id=@item FOR UPDATE";
            using (var reader = command.ExecuteReader())
            {
                if (item.IsDirty || !reader.Read() || reader.GetUInt64("owner") != source.Header.ReceiverId ||
                    reader.GetInt32("slot_type") != (int)SlotType.Auction ||
                    reader.GetUInt64("container_id") != item._holdingContainer.ContainerId ||
                    reader.GetInt32("count") != item.Count || reader.GetInt32("slot") != item.Slot)
                    throw new InvalidOperationException($"Legacy auction attachment {item.Id} has different persistent ownership or location.");
                var row = MailLifecycleStore.ReadItemRow(reader);
                _itemRows.Add(item.Id, JsonSerializer.Serialize(row));
            }
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@container", item._holdingContainer.ContainerId);
            command.CommandText = "SELECT slot_type, owner_id, container_type FROM item_containers WHERE container_id=@container FOR UPDATE";
            using (var reader = command.ExecuteReader())
            {
                if (item._holdingContainer.IsDirty || !reader.Read() || reader.GetInt32("slot_type") != (int)SlotType.Auction ||
                    reader.GetUInt32("owner_id") != item._holdingContainer.OwnerId ||
                    reader.GetString("container_type") != nameof(Items.Containers.ItemContainer))
                    throw new InvalidOperationException($"Legacy auction attachment {item.Id} has no matching Auction container.");
            }
            command.Parameters.AddWithValue("@slot", item.Slot);
            command.CommandText = "SELECT id FROM items WHERE container_id=@container AND slot=@slot FOR UPDATE";
            using (var reader = command.ExecuteReader())
                if (!reader.Read() || reader.GetUInt64(0) != item.Id || reader.Read())
                    throw new InvalidOperationException($"Legacy auction attachment {item.Id} shares its container slot.");

            CheckReferences(command, item.Id, true);
        }
    }

    internal void ValidateAfterSave(PersistenceSaveContext context)
    {
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        foreach (var item in attachments)
            CheckReferences(command, item.Id, false);
    }

    private void CheckReferences(MySqlCommand command, ulong itemId, bool sourceMustExist)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@item", itemId);
        command.CommandText = """
            SELECT id FROM mails WHERE @item IN
                (attachment0,attachment1,attachment2,attachment3,attachment4,
                 attachment5,attachment6,attachment7,attachment8,attachment9) FOR UPDATE
            """;
        using (var reader = command.ExecuteReader())
        {
            // Before Save only the source exists. After Save no active mail can reference the archive.
            var found = reader.Read();
            if (sourceMustExist ? !found || reader.GetInt64(0) != source.Id || reader.Read() : found)
                throw new InvalidOperationException($"Legacy auction attachment {itemId} has unexpected mail references.");
        }
        command.CommandText = "SELECT id FROM auction_house WHERE item_id=@item FOR UPDATE";
        if (command.ExecuteScalar() != null)
            throw new InvalidOperationException($"Legacy auction attachment {itemId} still has an auction listing.");
        command.Parameters.AddWithValue("@mail", source.Id);
        command.CommandText = "SELECT mail_id FROM auction_mail_claims WHERE mail_id=@mail OR item_id=@item FOR UPDATE";
        if (command.ExecuteScalar() != null)
            throw new InvalidOperationException($"Legacy auction attachment {itemId} already has a claim receipt.");
    }
}
