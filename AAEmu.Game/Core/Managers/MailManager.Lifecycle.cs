using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.Game.Core.Managers;

public partial class MailManager
{
    internal Func<Action<PersistenceSaveContext>, bool> CommitLifecycle { get; set; } =
        write => SaveManager.Instance.TryCommitEconomy([], write);
    internal Func<Action<PersistenceSaveContext>, Action<PersistenceSaveContext>, bool> CommitAuctionArchive { get; set; } =
        (validate, write) => SaveManager.Instance.TryCommitMailArchive(validate, write);
    internal Action<string, Exception> LifecycleCommitFailure { get; set; } = Environment.FailFast;
    internal Func<uint, bool?> ActiveMailSender { get; set; } = MailLifecycleStore.IsActiveSender;

    internal static bool IsExpired(BaseMail mail, DateTime now) =>
        mail.Body.RecvDate <= now && now - mail.Body.RecvDate >= MailExpireDelay;

    public bool ReturnMail(Character receiver, long id)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (receiver == null || !_allPlayerMails.TryGetValue(id, out var mail) ||
                mail.Header.ReceiverId != receiver.Id)
                return false;
            return TransitionMail(mail, receiver, false, DateTime.UtcNow);
        }
    }

    internal bool ReturnMailToSender(BaseMail source)
    {
        lock (SaveManager.PersistenceSyncRoot)
            return TransitionMail(source, null, true, DateTime.UtcNow);
    }

    public bool ReturnDeletedCharacterMail(uint receiverId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (receiverId == 0)
                return false;
            var sources = _allPlayerMails.Values
                .Where(mail => mail.Header.ReceiverId == receiverId && mail.CanReturnMail())
                .OrderBy(mail => mail.Id).ToArray();
            foreach (var source in sources)
            {
                if (!_allPlayerMails.TryGetValue(source.Id, out var current) || !ReferenceEquals(source, current))
                    continue;
                if (source.Header.ReceiverId != receiverId ||
                    !TransitionMail(source, null, true, DateTime.UtcNow))
                    return false;
            }
            return true;
        }
    }

    private bool CanReturnToExistingSender(BaseMail mail)
    {
        var senderName = nameManager.GetCharacterName(mail.Header.SenderId);
        return mail.CanReturnMail() && !string.IsNullOrEmpty(senderName) &&
            nameManager.GetCharacterId(senderName) == mail.Header.SenderId;
    }

    private bool TransitionMail(BaseMail source, Character actor, bool expired, DateTime now)
    {
        // Do not erase an unresolved item reference while creating the terminal snapshot.
        if (source.HasUnresolvedAttachments || !_allPlayerMails.TryGetValue(source.Id, out var current) ||
            !ReferenceEquals(source, current) || (!expired && (source.Body.RecvDate > now || IsExpired(source, now))))
            return false;
        var attachmentIds = new HashSet<ulong>();
        var auctionAttachments = new List<Item>();
        foreach (var item in source.Body.Attachments)
        {
            if (item == null || item.Id == 0 || item.Count <= 0 || !attachmentIds.Add(item.Id) ||
                item.OwnerId != source.Header.ReceiverId ||
                !ReferenceEquals(itemManager.GetItemByItemId(item.Id), item) ||
                item._holdingContainer == null ||
                item._holdingContainer.Items.Count(candidate => candidate.Id == item.Id) != 1 ||
                !item._holdingContainer.Items.Contains(item))
                return false;
            if (item.SlotType == SlotType.Mail && item._holdingContainer.ContainerType == SlotType.Mail &&
                item._holdingContainer.OwnerId == source.Header.ReceiverId)
                continue;
            if (!expired || !IsExpired(source, now) || source.MailType != MailType.AucBidWin ||
                item.SlotType != SlotType.Auction || item._holdingContainer.ContainerType != SlotType.Auction ||
                item.IsDirty || item._holdingContainer.IsDirty || item._holdingContainer.ContainerId == 0 ||
                !ReferenceEquals(itemManager.GetItemContainerByDbId(item._holdingContainer.ContainerId), item._holdingContainer) ||
                item._holdingContainer.Items.Count(candidate => candidate.Slot == item.Slot) != 1)
                return false;
            auctionAttachments.Add(item);
        }
        if (_allPlayerMails.Values.Any(mail => !ReferenceEquals(mail, source) &&
                mail.Body.Attachments.Any(item => attachmentIds.Contains(item.Id))))
            return false;
        var hasContents = source.GetTotalAttachmentCount() != 0;
        var shouldReturn = false;
        if (source.CanReturnMail() && (!expired || hasContents))
        {
            var senderActive = ActiveMailSender(source.Header.SenderId);
            if (senderActive == null || (senderActive.Value && !CanReturnToExistingSender(source)))
                return false;
            shouldReturn = senderActive.Value;
        }
        if (!expired && !shouldReturn)
            return false;
        var outcome = shouldReturn ? MailTerminalOutcome.Returned :
            hasContents ? MailTerminalOutcome.Archived : MailTerminalOutcome.Removed;
        var snapshot = MailLifecycleStore.Snapshot(source);

        using var inventory = new InventoryMutation(ItemTaskType.Mail);
        using var mutation = BeginMutation();
        if (!mutation.TryRemove(source))
            return false;
        BaseMail returned = null;
        if (shouldReturn)
        {
            returned = new BaseMail
            {
                MailType = source.MailType, ReceiverName = nameManager.GetCharacterName(source.Header.SenderId), Title = source.Title,
                Header = { SenderId = source.Header.ReceiverId, SenderName = source.ReceiverName,
                    ReceiverId = source.Header.SenderId, Returned = true, Status = MailStatus.Unread,
                    Extra = source.Header.Extra },
                Body = { Text = source.Body.Text, CopperCoins = source.Body.CopperCoins,
                    BillingAmount = source.Body.BillingAmount, MoneyAmount2 = source.Body.MoneyAmount2,
                    SendDate = now, RecvDate = now }
            };
            if (source.Body.Attachments.Count > 0)
            {
                var destination = itemManager.GetItemContainerForCharacter(returned.Header.ReceiverId, SlotType.Mail, null, 0);
                foreach (var item in source.Body.Attachments)
                {
                    if (!ReferenceEquals(itemManager.GetItemByItemId(item.Id), item) ||
                        !inventory.TryMove(item, destination))
                        return false; // A full destination retains the source for the next tick.
                    returned.Body.Attachments.Add(item);
                }
            }
            if (!mutation.TryAdd(returned))
                return false;
        }
        bool committed;
        try
        {
            if (auctionAttachments.Count > 0)
            {
                var archive = new LegacyAuctionMailArchive(source, auctionAttachments, now);
                committed = CommitAuctionArchive(archive.ValidateSource,
                    context => MailLifecycleStore.Write(context, source, snapshot, outcome, now, 0, actor?.Id ?? 0, archive));
            }
            else
                committed = CommitLifecycle(context => MailLifecycleStore.Write(context, source, snapshot,
                    outcome, now, returned?.Id ?? 0, actor?.Id ?? 0));
        }
        catch (Exception exception)
        {
            inventory.PreservePreparedState();
            mutation.PreservePreparedState();
            LifecycleCommitFailure($"Mail lifecycle {source.Id} has an unknown commit result. Restart before another save.", exception);
            throw;
        }
        if (!committed)
            return false;

        // A committed removal must never be restored by a notification exception.
        mutation.Complete(false);
        try
        {
            mailIdManager.RetainId(checked((uint)source.Id));
            if (returned != null)
                mailIdManager.RetainId(checked((uint)returned.Id));
            if (outcome == MailTerminalOutcome.Archived)
                foreach (var item in source.Body.Attachments)
                    itemManager.DetachArchivedMailItem(item);
        }
        catch (Exception exception)
        {
            inventory.PreservePreparedState();
            LifecycleCommitFailure($"Committed mail lifecycle {source.Id} could not enter live state. Restart before another save.", exception);
            throw;
        }
        try
        {
            inventory.Complete();
            var receiver = worldManager.GetCharacterById(source.Header.ReceiverId);
            if (receiver != null)
            {
                RecountUnread(receiver, now);
                if (returned != null)
                    receiver.SendPacket(new SCMailReturnedPacket(source.Id, returned.Header));
                else
                    receiver.SendPacket(new SCMailDeletedPacket(false, source.Id, true, receiver.Mails.UnreadMailCount));
                receiver.Mails.SendUnreadMailCount();
            }
            NotifyMailRemovedIfSenderOnline(source);
            if (returned != null)
                NotifyNewMailByNameIfOnline(returned, returned.ReceiverName);
        }
        catch (Exception exception)
        {
            inventory.PreservePreparedState();
            Logger.Error(exception, "Committed mail lifecycle notification failed for {0}.", source.Id);
        }
        return true;
    }

    private void RecountUnread(Character receiver, DateTime now)
    {
        receiver.Mails.UnreadMailCount.ResetReceived();
        foreach (var mail in _allPlayerMails.Values)
            if (mail.Header.ReceiverId == receiver.Id && mail.Header.Status != MailStatus.Read &&
                mail.Body.RecvDate <= now && !IsExpired(mail, now))
                receiver.Mails.UnreadMailCount.UpdateReceived(mail.MailType, 1);
    }
}
