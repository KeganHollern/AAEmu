using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Game.Mails;

/// <summary>
/// Stages new and removed mail records under the persistence lock. Attachment moves
/// belong to the caller's InventoryMutation. Only Complete publishes notifications.
/// </summary>
public sealed class MailMutation : IDisposable
{
    private readonly MailManager _manager;
    private readonly List<(BaseMail Mail, byte Attachments, bool Dirty)> _added = [];
    private readonly List<(BaseMail Mail, bool QueuedDeletion)> _removed = [];
    private bool _failed;
    private bool _finished;

    internal MailMutation(MailManager manager)
    {
        RequireLock();
        _manager = manager;
    }

    public bool TryAdd(BaseMail mail)
    {
        RequireActive();
        if (_failed || mail == null || mail.Id != 0 || !_manager.ValidateMutationMail(mail))
            return Fail();

        var attachments = mail.Header.Attachments;
        var dirty = mail.IsDirty;
        var id = _manager.ReserveMutationMailId();
        if (id == 0)
            return Fail();
        mail.Id = id;
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        if (!_manager._allPlayerMails.TryAdd(id, mail))
        {
            // A colliding allocated ID already belongs to live mail. Never release it.
            mail.Id = 0;
            mail.Header.Attachments = attachments;
            mail.IsDirty = dirty;
            return Fail();
        }
        _added.Add((mail, attachments, dirty));
        return true;
    }

    public bool TryRemove(BaseMail mail)
    {
        RequireActive();
        if (_failed || mail == null || _added.Any(entry => ReferenceEquals(entry.Mail, mail)) ||
            !_manager._allPlayerMails.TryGetValue(mail.Id, out var current) ||
            !ReferenceEquals(current, mail))
            return Fail();

        _manager._allPlayerMails.TryRemove(mail.Id, out _);
        _removed.Add((mail, _manager.QueueMutationDeletion(mail.Id)));
        return true;
    }

    public bool Complete()
    {
        RequireActive();
        if (_failed)
            return false;
        _finished = true;
        // Removed IDs stay reserved: a stale tax/payment request must never refer to
        // a newly issued mail in the same server lifetime.
        foreach (var (mail, _) in _removed)
            _manager.NotifyDeleteMailByNameIfOnline(mail, mail.ReceiverName);
        foreach (var (mail, _, _) in _added)
            _manager.NotifyNewMailByNameIfOnline(mail, mail.ReceiverName);
        return true;
    }

    public void PreservePreparedState()
    {
        RequireLock();
        _finished = true;
    }

    public void Dispose()
    {
        RequireLock();
        if (_finished)
            return;
        _finished = true;
        foreach (var (mail, attachments, dirty) in _added)
        {
            _manager._allPlayerMails.TryRemove(mail.Id, out _);
            _manager.ReleaseReservedMailId(checked((uint)mail.Id));
            mail.Id = 0;
            mail.Header.Attachments = attachments;
            mail.IsDirty = dirty;
        }
        foreach (var (mail, queuedDeletion) in _removed)
        {
            _manager._allPlayerMails.TryAdd(mail.Id, mail);
            if (queuedDeletion)
                _manager.RestoreMutationDeletion(mail.Id);
        }
    }

    private bool Fail() { _failed = true; return false; }

    private void RequireActive()
    {
        RequireLock();
        ObjectDisposedException.ThrowIf(_finished, this);
    }

    private static void RequireLock()
    {
        if (!Monitor.IsEntered(SaveManager.PersistenceSyncRoot))
            throw new InvalidOperationException("Mail mutations require the persistence lock.");
    }
}
