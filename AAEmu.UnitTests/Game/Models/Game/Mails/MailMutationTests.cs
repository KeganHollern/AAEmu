using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Mails;

public sealed class MailMutationTests
{
    private MailManager _manager;
    private Mock<IMailIdManager> _ids;
    private Mock<IItemManager> _items;
    private Mock<ISession> _session;
    private CharacterMock _receiver;
    private List<long> _deleted;

    [Before(Test)]
    public void SetUp()
    {
        _ids = Mock.Of<IMailIdManager>();
        _ids.GetNextId().Returns(10000U);
        _items = Mock.Of<IItemManager>();
        _session = Mock.Of<ISession>();
        _receiver = new CharacterMock
        {
            Id = 7, Name = "Receiver", Connection = new GameConnection(_session.Object)
        };
        _receiver.Mails = new CharacterMails(_receiver);
        var names = new NameManager();
        names.Load([], [], []);
        names.AddCharacter(7, "Receiver", 1);
        var world = Mock.Of<IWorldManager>();
        world.GetCharacter("Receiver").Returns(_receiver);
        world.GetCharacterById(7).Returns(_receiver);
        _manager = new MailManager(_ids.Object, names, _items.Object,
            Mock.Of<ITaskManager>().Object, world.Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object) { _allPlayerMails = [] };
        _deleted = [];
        typeof(MailManager).GetField("_deletedMailIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_manager, _deleted);
    }

    [Test]
    public async Task KnownFailure_RestoresSourceAndDeletionQueueAndReleasesOnlyNewId()
    {
        var previous = Mail();
        previous.Id = 1;
        _manager._allPlayerMails[1] = previous;
        _deleted.Add(2);
        var next = Mail();
        next.Header.Attachments = 9;
        next.IsDirty = false;
        bool staged;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            staged = mutation.TryRemove(previous) && mutation.TryAdd(next);
        }
        await Assert.That(staged).IsTrue();
        await Assert.That(_manager._allPlayerMails.Values).IsEquivalentTo([previous]);
        await Assert.That(_deleted).IsEquivalentTo([2L]);
        await Assert.That(next.Id).IsEqualTo(0L);
        await Assert.That(next.Header.Attachments).IsEqualTo((byte)9);
        await Assert.That(next.IsDirty).IsFalse();
        _ids.ReleaseId(10000U).WasCalled(Times.Once);
        _ids.ReleaseId(1U).WasCalled(Times.Never);
        _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task Complete_PublishesReplacementOnlyAfterBothRecordsArePrepared()
    {
        var previous = Mail();
        previous.Id = 1;
        previous.IsDelivered = true;
        _manager._allPlayerMails[1] = previous;
        var next = Mail();
        bool completed;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            mutation.TryRemove(previous);
            mutation.TryAdd(next);
            _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
            completed = mutation.Complete();
            mutation.PreservePreparedState();
        }
        await Assert.That(completed).IsTrue();
        await Assert.That(_manager._allPlayerMails.Values).IsEquivalentTo([next]);
        await Assert.That(_deleted).IsEquivalentTo([1L]);
        await Assert.That(next.IsDelivered).IsTrue();
        _session.SendPacket(Any<byte[]>()).WasCalled(Times.Exactly(2));
        _ids.ReleaseId(Any<uint>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task UncertainCommit_PreservesPreparedRecordsAndIdsWithoutNotifying()
    {
        var previous = Mail();
        previous.Id = 1;
        _manager._allPlayerMails[1] = previous;
        var next = Mail();
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            mutation.TryRemove(previous);
            mutation.TryAdd(next);
            mutation.PreservePreparedState();
            mutation.PreservePreparedState();
        }
        await Assert.That(_manager._allPlayerMails.Values).IsEquivalentTo([next]);
        await Assert.That(_deleted).IsEquivalentTo([1L]);
        await Assert.That(next.Id).IsEqualTo(10000L);
        await Assert.That(next.IsDelivered).IsFalse();
        _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        _ids.ReleaseId(Any<uint>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task RepeatedSourceRemoval_FailsAndRestoresTheOriginalMail()
    {
        var previous = Mail();
        previous.Id = 1;
        _manager._allPlayerMails[1] = previous;
        bool repeated;
        bool completed;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            mutation.TryRemove(previous);
            repeated = mutation.TryRemove(previous);
            completed = mutation.Complete();
        }
        await Assert.That(repeated).IsFalse();
        await Assert.That(completed).IsFalse();
        await Assert.That(_manager._allPlayerMails[1]).IsSameReferenceAs(previous);
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    [Arguments("duplicate")]
    [Arguments("owner")]
    [Arguments("container")]
    [Arguments("unregistered")]
    [Arguments("already-mailed")]
    public async Task InvalidAttachmentIdentity_IsRejectedBeforeMailAllocation(string invalid)
    {
        var item = Attachment();
        var mail = Mail();
        mail.Body.Attachments.Add(item);
        switch (invalid)
        {
            case "duplicate": mail.Body.Attachments.Add(item); break;
            case "owner": item.OwnerId = 8; break;
            case "container": item._holdingContainer.Items.Clear(); break;
            case "unregistered": _items.GetItemByItemId(item.Id).Returns((Item)null); break;
            case "already-mailed":
                var previous = Mail();
                previous.Id = 1;
                previous.Body.Attachments.Add(item);
                _manager._allPlayerMails[1] = previous;
                break;
        }
        bool added;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            added = mutation.TryAdd(mail);
        }
        await Assert.That(added).IsFalse();
        await Assert.That(mail.Id).IsEqualTo(0L);
        _ids.GetNextId().WasCalled(Times.Never);
        _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task ValidAttachment_KeepsExactContainerSlotAndItem()
    {
        var item = Attachment();
        item.Slot = 42;
        var mail = Mail();
        mail.Body.Attachments.Add(item);
        bool completed;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            completed = mutation.TryAdd(mail) && mutation.Complete();
        }
        await Assert.That(completed).IsTrue();
        await Assert.That(mail.Body.Attachments.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Slot).IsEqualTo(42);
        await Assert.That(item._holdingContainer.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)2);
    }

    [Test]
    public async Task InvalidReceiver_IsRejectedWithoutReservingId()
    {
        var mail = Mail();
        mail.ReceiverName = "Missing";
        bool added;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = _manager.BeginMutation();
            added = mutation.TryAdd(mail);
        }
        await Assert.That(added).IsFalse();
        _ids.GetNextId().WasCalled(Times.Never);
    }

    private BaseMail Mail() => new()
    {
        ReceiverName = "Receiver", Header = { ReceiverId = 7 }, MailType = MailType.SysExpress,
        Title = "Settlement", Body = { Text = "Settlement", CopperCoins = 100,
            SendDate = DateTime.UtcNow, RecvDate = DateTime.UtcNow }
    };

    private Item Attachment()
    {
        var container = new ItemContainer(7, SlotType.Mail, false, _receiver) { Owner = _receiver };
        var item = new Item { Id = 20, OwnerId = 7, SlotType = SlotType.Mail, Count = 1,
            _holdingContainer = container };
        container.Items.Add(item);
        _items.GetItemByItemId(20).Returns(item);
        return item;
    }
}
