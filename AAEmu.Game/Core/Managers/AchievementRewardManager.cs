using System.Diagnostics.CodeAnalysis;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

internal static class AchievementRewardManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public static AchievementRewardStatus? TryDeliver(
        Character character,
        uint achievementId,
        uint itemTemplateId)
    {
        ArgumentNullException.ThrowIfNull(character);

        // SaveManager holds this lock for its complete database transaction.
        // Use the same lock so reward delivery cannot overlap the normal save.
        lock (SaveManager.Instance.PersistenceSyncRoot)
            return TryDeliverLocked(character, achievementId, itemTemplateId);
    }

    private static AchievementRewardStatus? TryDeliverLocked(
        Character character,
        uint achievementId,
        uint itemTemplateId)
    {
        var itemManager = ItemManager.Instance;
        var mailManager = MailManager.Instance;
        var stagedItem = default(Item);
        var inventoryPlan = default(InventoryDeliveryPlan);
        var rewardMail = default(BaseMail);
        ulong reservedItemId = 0;
        uint reservedMailId = 0;
        var commitAttempted = false;
        var transactionCommitted = false;
        var committedStatus = AchievementRewardStatus.Pending;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();

            var status = ReadRewardStatus(connection, transaction, character.Id, achievementId);
            if (status == null)
            {
                transaction.Rollback();
                Logger.Error(
                    "Missing completion row for achievement reward {AchievementId}, character {CharacterId}",
                    achievementId,
                    character.Id);
                return null;
            }

            if (status != AchievementRewardStatus.Pending)
            {
                transaction.Commit();
                return status;
            }

            stagedItem = itemManager.Create(itemTemplateId, 1, 0, false);
            if (stagedItem == null)
            {
                transaction.Rollback();
                Logger.Error(
                    "Missing item template {ItemTemplateId} for achievement {AchievementId}, character {CharacterId}",
                    itemTemplateId,
                    achievementId,
                    character.Id);
                return null;
            }

            PrepareItemDefaults(stagedItem);

            if (TryCreateInventoryPlan(character.Inventory?.Bag, stagedItem, out inventoryPlan))
            {
                if (inventoryPlan.Stack != null)
                {
                    ReplaceStack(connection, transaction, inventoryPlan);
                }
                else
                {
                    reservedItemId = itemManager.ReserveItemId();
                    PrepareNewItem(
                        stagedItem,
                        reservedItemId,
                        character.Id,
                        SlotType.Inventory,
                        inventoryPlan.Slot,
                        character.Inventory.Bag);
                    InsertItem(connection, transaction, stagedItem);
                }

                committedStatus = AchievementRewardStatus.Inventory;
            }
            else
            {
                reservedItemId = itemManager.ReserveItemId();
                reservedMailId = mailManager.GetNewMailId();
                var mailSlot = GetMailAttachmentSlot(character.Inventory.MailAttachments);
                PrepareNewItem(
                    stagedItem,
                    reservedItemId,
                    character.Id,
                    SlotType.Mail,
                    mailSlot,
                    character.Inventory.MailAttachments);
                rewardMail = CreateRewardMail(character, reservedMailId, achievementId, stagedItem);

                // This order matches SaveManager: mail rows, item rows, character rows.
                InsertMail(connection, transaction, rewardMail);
                InsertItem(connection, transaction, stagedItem);
                committedStatus = AchievementRewardStatus.Mail;
            }

            // Keep this update last. If another server wins the reward race, this
            // conditional update fails and this transaction rolls back its item or mail.
            SetRewardStatus(
                connection,
                transaction,
                character.Id,
                achievementId,
                committedStatus);
            commitAttempted = true;
            transaction.Commit();
            transactionCommitted = true;

            try
            {
                ApplyCommittedDelivery(
                    character,
                    itemManager,
                    mailManager,
                    committedStatus,
                    stagedItem,
                    inventoryPlan,
                    rewardMail);
            }
            catch (Exception ex)
            {
                StopForConsistencyFailure(
                    $"Committed achievement reward {achievementId} for character {character.Id} could not enter live state. The Game service must restart before another save.",
                    ex);
            }

            character.SendPacket(new SCAchievementItemSentPacket(
                achievementId,
                committedStatus == AchievementRewardStatus.Mail,
                itemTemplateId));
            return committedStatus;
        }
        catch (Exception ex)
        {
            Logger.Error(
                ex,
                "Failed to deliver achievement reward {AchievementId} to character {CharacterId}",
                achievementId,
                character.Id);

            if (!transactionCommitted && !commitAttempted)
            {
                if (reservedItemId != 0)
                    itemManager.ReleaseReservedItemId(reservedItemId);
                if (reservedMailId != 0)
                    mailManager.ReleaseReservedMailId(reservedMailId);
            }

            // A connection failure during Commit leaves the transaction result
            // unknown. Keep reserved IDs and reload the character so committed
            // rows cannot conflict with IDs that this process reuses.
            if (!transactionCommitted && commitAttempted)
            {
                StopForConsistencyFailure(
                    $"Achievement reward {achievementId} for character {character.Id} has an unknown commit result. The Game service must restart before another save.",
                    ex);
            }

            return transactionCommitted ? committedStatus : null;
        }
    }

    [DoesNotReturn]
    private static void StopForConsistencyFailure(string message, Exception exception)
    {
        Logger.Fatal(exception, message);
        Environment.FailFast(message, exception);
    }

    private static AchievementRewardStatus? ReadRewardStatus(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint characterId,
        uint achievementId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT `reward_status` FROM `character_achievements` " +
            "WHERE `character_id` = @character_id AND `achievement_id` = @achievement_id";
        command.Parameters.AddWithValue("@character_id", characterId);
        command.Parameters.AddWithValue("@achievement_id", achievementId);
        var value = command.ExecuteScalar();
        if (value == null || value == DBNull.Value)
            return null;

        var rawStatus = Convert.ToByte(value);
        return Enum.IsDefined(typeof(AchievementRewardStatus), rawStatus)
            ? (AchievementRewardStatus)rawStatus
            : null;
    }

    internal static bool TryCreateInventoryPlan(
        ItemContainer bag,
        Item stagedItem,
        out InventoryDeliveryPlan plan)
    {
        plan = null;
        if (bag == null)
            return false;

        bag.SpaceLeftForItem(stagedItem, out var currentItems);
        var stack = currentItems
            .Where(item => item.Count + stagedItem.Count <= item.Template.MaxCount)
            .OrderBy(item => item.Slot)
            .FirstOrDefault();
        if (stack != null)
        {
            plan = new InventoryDeliveryPlan(
                stack,
                stack.Count,
                stack.Count + stagedItem.Count,
                stack.Slot);
            return true;
        }

        var slot = bag.GetUnusedSlot(-1);
        if (slot < 0)
            return false;

        plan = new InventoryDeliveryPlan(null, 0, stagedItem.Count, slot);
        return true;
    }

    internal static int GetMailAttachmentSlot(ItemContainer mailAttachments)
    {
        var slot = mailAttachments?.GetUnusedSlot(-1) ?? -1;
        if (slot < 0)
            throw new InvalidOperationException("The live mail attachment container has no free slot.");
        return slot;
    }

    private static void PrepareItemDefaults(Item item)
    {
        if (item.Template.ExpAbsLifetime > 0)
            item.ExpirationTime = DateTime.UtcNow.AddMinutes(item.Template.ExpAbsLifetime);
        if (item.Template.ExpOnlineLifetime > 0)
            item.ExpirationOnlineMinutesLeft = item.Template.ExpOnlineLifetime;
        if (item.Template.ExpDate > DateTime.MinValue)
            item.ExpirationTime = item.Template.ExpDate;

        if (item is EquipItem equipItem && item.Template is EquipItemTemplate equipItemTemplate)
        {
            equipItem.ChargeCount = equipItemTemplate.ChargeCount;
            if (equipItemTemplate.ChargeLifetime > 0 &&
                !equipItemTemplate.BindType.HasFlag(ItemBindType.BindOnUnpack))
                equipItem.ChargeStartTime = DateTime.UtcNow;
        }
    }

    private static void PrepareNewItem(
        Item item,
        ulong itemId,
        uint ownerId,
        SlotType slotType,
        int slot,
        ItemContainer container)
    {
        item.Id = itemId;
        item.OwnerId = ownerId;
        item.SlotType = slotType;
        item.Slot = slot;
        item._holdingContainer = container;
    }

    internal static BaseMail CreateRewardMail(
        Character character,
        uint mailId,
        uint achievementId,
        Item item)
    {
        var now = DateTime.UtcNow;
        var achievementName = LocalizationManager.Instance.Get(
            "achievements",
            "name",
            achievementId,
            $"Achievement {achievementId}");
        var itemName = LocalizationManager.Instance.Get(
            "items",
            "name",
            item.TemplateId,
            $"Item {item.TemplateId}");
        var (title, body) = CreateRewardMailContent(achievementName, itemName);
        var mail = new BaseMail
        {
            Id = mailId,
            MailType = MailType.SysExpress,
            ReceiverName = character.Name,
            Title = title,
            OpenDate = now
        };
        mail.Header.Status = MailStatus.Unread;
        mail.Header.SenderId = 0;
        mail.Header.SenderName = ".achievement";
        mail.Header.ReceiverId = character.Id;
        mail.Header.Attachments = 1;
        mail.Body.Text = body;
        mail.Body.SendDate = now;
        mail.Body.RecvDate = now;
        mail.Body.Attachments.Add(item);
        return mail;
    }

    internal static (string Title, string Body) CreateRewardMailContent(
        string achievementName,
        string itemName)
    {
        return (
            $"title('{EscapeLuaString(achievementName)}')",
            $"body('{EscapeLuaString(achievementName)}','{EscapeLuaString(itemName)}')");
    }

    internal static string EscapeLuaString(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static void RegisterItemInContainer(
        ItemManager itemManager,
        ItemContainer container,
        Item item,
        int slot)
    {
        if (container.GetItemBySlot(slot) != null)
            throw new InvalidOperationException(
                $"Reward item {item.Id} slot {slot} became occupied before the committed item entered live state.");

        if (!itemManager.AddItem(item))
            throw new InvalidOperationException($"Reward item {item.Id} already exists in memory.");

        if (!container.AddOrMoveExistingItem(ItemTaskType.Invalid, item, slot, false))
            throw new InvalidOperationException($"Reward item {item.Id} could not enter its live container.");

        if (item.Slot != slot || !ReferenceEquals(item._holdingContainer, container) ||
            !ReferenceEquals(container.GetItemBySlot(slot), item))
        {
            throw new InvalidOperationException(
                $"Reward item {item.Id} entered a different live slot than its committed slot {slot}.");
        }
    }

    private static void ReplaceStack(
        MySqlConnection connection,
        MySqlTransaction transaction,
        InventoryDeliveryPlan plan)
    {
        WriteItem(connection, transaction, plan.Stack, plan.NewCount, true);
    }

    private static void InsertItem(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Item item)
    {
        // Item IDs can be recycled before SaveManager removes the old row.
        // Match normal item saves so a stale row cannot block reward delivery forever.
        WriteItem(connection, transaction, item, item.Count, true);
    }

    private static void WriteItem(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Item item,
        int count,
        bool replace)
    {
        var details = new PacketStream();
        item.WriteDetails(details);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            (replace ? "REPLACE INTO `items` (" : "INSERT INTO `items` (") +
            "`id`,`type`,`template_id`,`container_id`,`slot_type`,`slot`,`count`,`details`,`lifespan_mins`,`made_unit_id`," +
            "`unsecure_time`,`unpack_time`,`owner`,`created_at`,`grade`,`flags`,`ucc`," +
            "`expire_time`,`expire_online_minutes`,`charge_time`,`charge_count`) " +
            "VALUES (" +
            "@id,@type,@template_id,@container_id,@slot_type,@slot,@count,@details,@lifespan_mins,@made_unit_id," +
            "@unsecure_time,@unpack_time,@owner,@created_at,@grade,@flags,@ucc," +
            "@expire_time,@expire_online_minutes,@charge_time,@charge_count)";
        command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@type", item.GetType().ToString());
        command.Parameters.AddWithValue("@template_id", item.TemplateId);
        command.Parameters.AddWithValue("@container_id", item._holdingContainer?.ContainerId ?? 0);
        command.Parameters.AddWithValue("@slot_type", (int)item.SlotType);
        command.Parameters.AddWithValue("@slot", item.Slot);
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@details", details.GetBytes());
        command.Parameters.AddWithValue("@lifespan_mins", item.LifespanMins);
        command.Parameters.AddWithValue("@made_unit_id", item.MadeUnitId);
        command.Parameters.AddWithValue("@unsecure_time", item.UnsecureTime);
        command.Parameters.AddWithValue("@unpack_time", item.UnpackTime);
        command.Parameters.AddWithValue("@owner", item.OwnerId);
        command.Parameters.AddWithValue("@created_at", item.CreateTime);
        command.Parameters.AddWithValue("@grade", item.Grade);
        command.Parameters.AddWithValue("@flags", (byte)item.ItemFlags);
        command.Parameters.AddWithValue("@ucc", item.UccId);
        command.Parameters.AddWithValue("@expire_time", item.ExpirationTime);
        command.Parameters.AddWithValue("@expire_online_minutes", item.ExpirationOnlineMinutesLeft);
        command.Parameters.AddWithValue("@charge_time", item.ChargeStartTime);
        command.Parameters.AddWithValue("@charge_count", item.ChargeCount);
        if (command.ExecuteNonQuery() < 1)
            throw new InvalidOperationException($"Reward item {item.Id} was not saved.");
    }

    private static void InsertMail(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BaseMail mail)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Mail IDs can be recycled before SaveManager removes the old row.
        command.CommandText =
            "REPLACE INTO `mails` (" +
            "`id`,`type`,`status`,`title`,`text`,`sender_id`,`sender_name`,`attachment_count`," +
            "`receiver_id`,`receiver_name`,`open_date`,`send_date`,`received_date`,`returned`,`extra`," +
            "`money_amount_1`,`money_amount_2`,`money_amount_3`," +
            "`attachment0`,`attachment1`,`attachment2`,`attachment3`,`attachment4`," +
            "`attachment5`,`attachment6`,`attachment7`,`attachment8`,`attachment9`) " +
            "VALUES (" +
            "@id,@type,@status,@title,@text,@sender_id,@sender_name,@attachment_count," +
            "@receiver_id,@receiver_name,@open_date,@send_date,@received_date,@returned,@extra," +
            "@money_amount_1,@money_amount_2,@money_amount_3," +
            "@attachment0,@attachment1,@attachment2,@attachment3,@attachment4," +
            "@attachment5,@attachment6,@attachment7,@attachment8,@attachment9)";
        command.Parameters.AddWithValue("@id", mail.Id);
        command.Parameters.AddWithValue("@type", (byte)mail.MailType);
        command.Parameters.AddWithValue("@status", (byte)mail.Header.Status);
        command.Parameters.AddWithValue("@title", mail.Title);
        command.Parameters.AddWithValue("@text", mail.Body.Text);
        command.Parameters.AddWithValue("@sender_id", mail.Header.SenderId);
        command.Parameters.AddWithValue("@sender_name", mail.Header.SenderName);
        command.Parameters.AddWithValue("@attachment_count", mail.Header.Attachments);
        command.Parameters.AddWithValue("@receiver_id", mail.Header.ReceiverId);
        command.Parameters.AddWithValue("@receiver_name", mail.ReceiverName);
        command.Parameters.AddWithValue("@open_date", mail.OpenDate);
        command.Parameters.AddWithValue("@send_date", mail.Body.SendDate);
        command.Parameters.AddWithValue("@received_date", mail.Body.RecvDate);
        command.Parameters.AddWithValue("@returned", mail.Header.Returned ? 1 : 0);
        command.Parameters.AddWithValue("@extra", mail.Header.Extra);
        command.Parameters.AddWithValue("@money_amount_1", mail.Body.CopperCoins);
        command.Parameters.AddWithValue("@money_amount_2", mail.Body.BillingAmount);
        command.Parameters.AddWithValue("@money_amount_3", mail.Body.MoneyAmount2);
        for (var index = 0; index < MailBody.MaxMailAttachments; index++)
        {
            command.Parameters.AddWithValue(
                $"@attachment{index}",
                index < mail.Body.Attachments.Count ? mail.Body.Attachments[index].Id : 0);
        }

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Reward mail {mail.Id} was not saved.");
    }

    private static void SetRewardStatus(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint characterId,
        uint achievementId,
        AchievementRewardStatus status)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE `character_achievements` SET `reward_status` = @reward_status " +
            "WHERE `character_id` = @character_id AND `achievement_id` = @achievement_id " +
            "AND `reward_status` = 0";
        command.Parameters.AddWithValue("@reward_status", (byte)status);
        command.Parameters.AddWithValue("@character_id", characterId);
        command.Parameters.AddWithValue("@achievement_id", achievementId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                $"Reward status for achievement {achievementId}, character {characterId} was not saved.");
    }

    private static void ApplyCommittedDelivery(
        Character character,
        ItemManager itemManager,
        MailManager mailManager,
        AchievementRewardStatus status,
        Item stagedItem,
        InventoryDeliveryPlan plan,
        BaseMail rewardMail)
    {
        if (status == AchievementRewardStatus.Mail)
        {
            RegisterItemInContainer(
                itemManager,
                character.Inventory.MailAttachments,
                stagedItem,
                stagedItem.Slot);
            if (!mailManager.TryAddPlayerMail(rewardMail))
                throw new InvalidOperationException($"Reward mail {rewardMail.Id} already exists in memory.");

            stagedItem.IsDirty = false;
            rewardMail.IsDirty = false;
            mailManager.NotifyNewMailByNameIfOnline(rewardMail, character.Name);
            return;
        }

        ItemTask itemTask;
        Item deliveredItem;
        if (plan.Stack != null)
        {
            if (plan.Stack.Count != plan.OldCount ||
                plan.Stack.Slot != plan.Slot ||
                !ReferenceEquals(plan.Stack._holdingContainer, character.Inventory.Bag))
            {
                throw new InvalidOperationException(
                    $"Reward stack {plan.Stack.Id} changed before its committed count entered live state.");
            }

            plan.Stack.Count = plan.NewCount;
            plan.Stack.IsDirty = false;
            deliveredItem = plan.Stack;
            itemTask = new ItemCountUpdate(plan.Stack, stagedItem.Count);
        }
        else
        {
            RegisterItemInContainer(
                itemManager,
                character.Inventory.Bag,
                stagedItem,
                plan.Slot);
            stagedItem.IsDirty = false;
            deliveredItem = stagedItem;
            itemTask = new ItemAdd(stagedItem);
        }

        character.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.TodReward, itemTask, []));
        character.Inventory.OnAcquiredItem(deliveredItem, stagedItem.Count, plan.Stack != null);
    }

    internal sealed record InventoryDeliveryPlan(
        Item Stack,
        int OldCount,
        int NewCount,
        int Slot);
}
