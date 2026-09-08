using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;

namespace AAEmu.Game.Models.Game.Mails;

internal static class PlayerMailSendExecutor
{
    internal static MailResult Execute(Character sender, MailType type, string receiverName,
        string title, string text, int copper, int billing, int otherMoney,
        IReadOnlyList<(SlotType Type, byte Slot)> requestedSlots, MailManager mails,
        IItemManager items, INameManager names, Func<bool> commit)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (type is not (MailType.Normal or MailType.Express) || copper < 0 ||
                billing != 0 || otherMoney != 0 || requestedSlots == null ||
                requestedSlots.Count > MailBody.MaxMailAttachments)
                return MailResult.InvalidLetterFormat;
            if (sender?.Inventory?.Bag == null)
                return MailResult.IncorrectItemInformation;
            if (string.IsNullOrWhiteSpace(receiverName))
                return MailResult.UnableToFindRecipient;
            var receiverId = names.GetCharacterId(receiverName);
            if (receiverId == 0)
                return MailResult.UnableToFindRecipient;
            var canonicalReceiver = names.GetCharacterName(receiverId);

            var selected = new List<Item>();
            var slots = new HashSet<byte>();
            var ids = new HashSet<ulong>();
            foreach (var (slotType, slot) in requestedSlots)
            {
                if (slotType == SlotType.None)
                    continue;
                if (slotType != SlotType.Inventory || !slots.Add(slot))
                    return MailResult.InvalidSlot;
                var item = sender.Inventory.Bag.GetItemBySlot(slot);
                if (item == null || item.Id == 0 || item.Count <= 0 || !ids.Add(item.Id) ||
                    item.OwnerId != sender.Id || item.SlotType != SlotType.Inventory ||
                    item._holdingContainer != sender.Inventory.Bag ||
                    !ReferenceEquals(items.GetItemByItemId(item.Id), item))
                    return MailResult.IncorrectItemInformation;
                if (item.HasFlag(ItemFlag.SoulBound))
                    return MailResult.BoundItem;
                selected.Add(item);
            }

            if (!TryGetTotalCost(type, selected.Count, copper, out var totalCost))
                return MailResult.InvalidLetterFormat;
            if (sender.Money < totalCost)
                return MailResult.InsufficientCoins;

            var now = DateTime.UtcNow;
            var mail = new BaseMail
            {
                MailType = type,
                ReceiverName = canonicalReceiver,
                Title = title,
                Header = { SenderId = sender.Id, SenderName = sender.Name, ReceiverId = receiverId },
                Body = { Text = text, CopperCoins = copper, SendDate = now,
                    RecvDate = type == MailType.Normal ? now + MailManager.NormalMailDelay : now }
            };
            using var inventory = new InventoryMutation(ItemTaskType.Mail);
            using var mailMutation = mails.BeginMutation();
            if (!inventory.TryChangeMoney(sender, -totalCost))
                return MailResult.InsufficientCoins;
            if (selected.Count > 0)
            {
                var destination = items.GetItemContainerForCharacter(receiverId, SlotType.Mail, null, 0);
                foreach (var item in selected)
                {
                    if (!inventory.TryMove(item, destination))
                        return MailResult.InvalidSlot;
                    mail.Body.Attachments.Add(item);
                }
            }
            if (!mailMutation.TryAdd(mail))
                return MailResult.MailErrorOccurred;

            try
            {
                if (!commit())
                    return MailResult.MailErrorOccurred;
                inventory.Complete();
                mailMutation.Complete();
                sender.SendPacket(new SCMailSentPacket(mail.Header,
                    requestedSlots.Select(slot => (slot.Type, slot.Slot)).ToArray()));
                return MailResult.Success;
            }
            catch
            {
                // A thrown commit has an uncertain result; a thrown notification is
                // already committed. Neither permits restoring the prepared assets.
                inventory.PreservePreparedState();
                mailMutation.PreservePreparedState();
                throw;
            }
        }
    }

    private static bool TryGetTotalCost(MailType type, int itemCount, int copper, out int cost)
    {
        cost = 0;
        var baseFee = type == MailType.Normal ? MailManager.CostNormal : MailManager.CostExpress;
        var attachmentFee = type == MailType.Normal ? MailManager.CostNormalAttachment : MailManager.CostExpressAttachment;
        if (baseFee < 0 || attachmentFee < 0 || MailManager.CostFreeAttachmentCount < 0)
            return false;
        var paidAttachments = Math.Max(0L, itemCount + (copper > 0 ? 1L : 0L) - MailManager.CostFreeAttachmentCount);
        var total = (long)baseFee + paidAttachments * attachmentFee + copper;
        if (total > int.MaxValue)
            return false;
        cost = (int)total;
        return true;
    }
}
