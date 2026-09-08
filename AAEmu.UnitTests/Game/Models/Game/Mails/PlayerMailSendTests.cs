using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Mails;

[NotInParallel]
public sealed class PlayerMailSendTests
{
    private ItemManager _previousItems;
    private QuestManager _previousQuests;
    private ItemManager _items;
    private MailManager _mails;
    private NameManager _names;
    private MailIds _ids;
    private CharacterMock _sender;
    private CharacterMock _receiver;
    private Mock<ISession> _senderSession;
    private Mock<ISession> _receiverSession;
    private Dictionary<ulong, Item> _allItems;
    private int _commitCalls;
    private Func<bool> _commit;

    [Before(Test)]
    public void SetUp()
    {
        _previousItems = SwapSingleton<ItemManager>(null);
        _previousQuests = SwapSingleton<QuestManager>(null);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        SwapSingleton(_items);
        SwapSingleton(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_removedItems", new List<ulong>());
        var containers = new Dictionary<ulong, ItemContainer>();
        SetField(_items, "_allPersistentContainers", containers);
        _senderSession = Mock.Of<ISession>();
        _receiverSession = Mock.Of<ISession>();
        _sender = Character(1, "Sender", _senderSession);
        _receiver = Character(2, "Receiver", _receiverSession);
        ulong nextContainer = 1;
        foreach (var character in new[] { _sender, _receiver })
        {
            foreach (var type in Enum.GetValues<SlotType>())
            {
                if (type == SlotType.EquipmentMate)
                    continue;
                var container = new ItemContainer(character.Id, type, false, character)
                    { ContainerId = nextContainer++, Owner = character };
                containers.Add(container.ContainerId, container);
            }
            character.Inventory = new Inventory(character);
        }
        _names = new NameManager();
        _names.Load([], [], []);
        _names.AddCharacter(1, "Sender", 1);
        _names.AddCharacter(2, "Receiver", 2);
        _ids = new MailIds();
        var world = Mock.Of<IWorldManager>();
        world.GetCharacter("Receiver").Returns(_receiver);
        _mails = new MailManager(_ids, _names, _items, Mock.Of<ITaskManager>().Object,
            world.Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object) { _allPlayerMails = [] };
        _commitCalls = 0;
        _commit = () => true;
    }

    [After(Test)]
    public void TearDown()
    {
        SwapSingleton(_previousItems);
        SwapSingleton(_previousQuests);
    }

    public static IEnumerable<MailType> NonPlayerTypes() =>
        Enum.GetValues<MailType>().Where(type => type is not (MailType.Normal or MailType.Express))
            .Append((MailType)255);

    [Test]
    [MethodDataSource(nameof(NonPlayerTypes))]
    public async Task NonPlayerType_RejectsBeforeAnyDebitAllocationOrSave(MailType type)
    {
        var result = Send(type: type, copper: 100);
        await Assert.That(result).IsEqualTo(MailResult.InvalidLetterFormat);
        await Assert.That(_sender.Money).IsEqualTo(1000L);
        await Assert.That(_commitCalls).IsEqualTo(0);
        await Assert.That(_mails._allPlayerMails).IsEmpty();
        _senderSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        _receiverSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    [Arguments(-1, 0, 0)]
    [Arguments(int.MinValue, 0, 0)]
    [Arguments(0, -1, 0)]
    [Arguments(0, 1, 0)]
    [Arguments(0, int.MaxValue, 0)]
    [Arguments(0, 0, -1)]
    [Arguments(0, 0, 1)]
    [Arguments(0, 0, int.MaxValue)]
    public async Task InvalidCurrencyFields_RejectWithoutMutation(int copper, int billing, int other)
    {
        var result = Send(copper: copper, billing: billing, other: other);
        await Assert.That(result).IsEqualTo(MailResult.InvalidLetterFormat);
        await Assert.That(_sender.Money).IsEqualTo(1000L);
        await Assert.That(_commitCalls).IsEqualTo(0);
    }

    [Test]
    public async Task FeePlusAttachmentOverflowsPacketAmount_RejectsEvenWithLargeWallet()
    {
        _sender.Money = long.MaxValue;
        var result = Send(copper: int.MaxValue);
        await Assert.That(result).IsEqualTo(MailResult.InvalidLetterFormat);
        await Assert.That(_sender.Money).IsEqualTo(long.MaxValue);
        await Assert.That(_commitCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DuplicateSlots_LeaveExactItemAndMoneyInSenderBag()
    {
        var item = AddItem(1, 0);
        var result = Send(slots: [(SlotType.Inventory, (byte)0), (SlotType.Inventory, (byte)0)]);
        await Assert.That(result).IsEqualTo(MailResult.InvalidSlot);
        await Assert.That(_sender.Inventory.Bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_sender.Money).IsEqualTo(1000L);
        await Assert.That(_receiver.Inventory.MailAttachments.Items).IsEmpty();
        await Assert.That(_commitCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("owner")]
    [Arguments("registration")]
    [Arguments("container")]
    [Arguments("bound")]
    public async Task InvalidAttachment_RejectsExactOwnershipAndTransferState(string invalid)
    {
        var item = AddItem(1, 0);
        switch (invalid)
        {
            case "owner": item.OwnerId = _receiver.Id; break;
            case "registration": _allItems[1] = new Item { Id = 1 }; break;
            case "container": item._holdingContainer = _receiver.Inventory.MailAttachments; break;
            case "bound": item.SetFlag(ItemFlag.SoulBound); break;
        }
        var result = Send(slots: [(SlotType.Inventory, (byte)0)]);
        await Assert.That(result).IsNotEqualTo(MailResult.Success);
        await Assert.That(_sender.Inventory.Bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_sender.Money).IsEqualTo(1000L);
        await Assert.That(_commitCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SuccessfulSend_PreparesFullExchangeBeforeSaveAndEveryNotice()
    {
        var first = AddItem(1, 0);
        var second = AddItem(2, 1);
        var prepared = false;
        _commit = () =>
        {
            prepared = _sender.Money == 540 && _sender.Inventory.Bag.Items.Count == 0 &&
                _receiver.Inventory.MailAttachments.Items.Count == 2 &&
                _mails._allPlayerMails.Values.Single().Body.CopperCoins == 200;
            _senderSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
            _receiverSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
            return true;
        };
        var result = Send(copper: 200, slots: [(SlotType.Inventory, (byte)0), (SlotType.Inventory, (byte)1)]);
        await Assert.That(result).IsEqualTo(MailResult.Success);
        await Assert.That(prepared).IsTrue();
        await Assert.That(_commitCalls).IsEqualTo(1);
        var mail = _mails._allPlayerMails.Values.Single();
        await Assert.That(mail.Body.Attachments.SequenceEqual([first, second])).IsTrue();
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)3);
        await Assert.That(mail.Header.Extra).IsEqualTo(0L);
        await Assert.That(first.OwnerId).IsEqualTo(2UL);
        await Assert.That(mail.IsDelivered).IsTrue();
    }

    [Test]
    public async Task FailureOnSecondMove_RestoresBothItemsDirtyStateAndMoney()
    {
        var first = AddItem(1, 0);
        var second = AddItem(2, 1);
        first.IsDirty = false;
        second.IsDirty = false;
        _receiver.Inventory.MailAttachments.ContainerSize = 1;
        var result = Send(slots: [(SlotType.Inventory, (byte)0), (SlotType.Inventory, (byte)1)]);
        await Assert.That(result).IsEqualTo(MailResult.InvalidSlot);
        await Assert.That(_sender.Inventory.Bag.Items.SequenceEqual([first, second])).IsTrue();
        await Assert.That(first.IsDirty).IsFalse();
        await Assert.That(second.IsDirty).IsFalse();
        await Assert.That(_sender.Money).IsEqualTo(1000L);
        await Assert.That(_receiver.Inventory.MailAttachments.Items).IsEmpty();
        await Assert.That(_mails._allPlayerMails).IsEmpty();
        await Assert.That(_commitCalls).IsEqualTo(0);
        _senderSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        _receiverSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task KnownSaveFailure_RestoresAttachmentMoneyMailAndAllowsOneRetry()
    {
        var item = AddItem(1, 0);
        _commit = () => false;
        var failed = Send(copper: 200, slots: [(SlotType.Inventory, (byte)0)]);
        var restored = _sender.Money == 1000 && item.OwnerId == 1 && item.Slot == 0 &&
            ReferenceEquals(item._holdingContainer, _sender.Inventory.Bag) && _mails._allPlayerMails.IsEmpty;
        _senderSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        _receiverSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        _commit = () => true;
        var retried = Send(copper: 200, slots: [(SlotType.Inventory, (byte)0)]);
        await Assert.That(failed).IsEqualTo(MailResult.MailErrorOccurred);
        await Assert.That(restored).IsTrue();
        await Assert.That(retried).IsEqualTo(MailResult.Success);
        await Assert.That(_sender.Money).IsEqualTo(620L);
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(1);
        await Assert.That(_ids.Released.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownCommitResult_RetainsPreparedExchangeWithoutSuccessPackets()
    {
        var item = AddItem(1, 0);
        _commit = () => throw new IOException("Commit response lost");
        var threw = false;
        try { Send(copper: 200, slots: [(SlotType.Inventory, (byte)0)]); }
        catch (IOException) { threw = true; }
        await Assert.That(threw).IsTrue();
        await Assert.That(_sender.Money).IsEqualTo(620L);
        await Assert.That(item.OwnerId).IsEqualTo(2UL);
        await Assert.That(_mails._allPlayerMails.Values.Single().Body.Attachments.Single()).IsSameReferenceAs(item);
        await Assert.That(_ids.Released).IsEmpty();
        _senderSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        _receiverSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task SimultaneousAttachmentReplay_ChargesAndTransfersExactlyOnce()
    {
        var item = AddItem(1, 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            Send(copper: 200, slots: [(SlotType.Inventory, (byte)0)]))));
        await Assert.That(results.Count(result => result == MailResult.Success)).IsEqualTo(1);
        await Assert.That(_sender.Money).IsEqualTo(620L);
        await Assert.That(_mails._allPlayerMails.Values.Single().Body.Attachments.Single()).IsSameReferenceAs(item);
        await Assert.That(_commitCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NotificationThrowsAfterCommit_KeepsAllCommittedParticipants(bool receiverThrows)
    {
        var item = AddItem(1, 0);
        (receiverThrows ? _receiver : _sender).Connection = new GameConnection(new ThrowingSession());
        var threw = false;
        try { Send(copper: 200, slots: [(SlotType.Inventory, (byte)0)]); }
        catch (IOException) { threw = true; }
        await Assert.That(threw).IsTrue();
        await Assert.That(_commitCalls).IsEqualTo(1);
        await Assert.That(_sender.Money).IsEqualTo(620L);
        await Assert.That(_sender.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_receiver.Inventory.MailAttachments.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_mails._allPlayerMails.Values.Single().Body.Attachments.Single()).IsSameReferenceAs(item);
        await Assert.That(_ids.Released).IsEmpty();
    }

    [Test]
    [Arguments(379, false)]
    [Arguments(380, true)]
    public async Task FeeAndMoney_RequireTheFullAmountBeforeAnyMove(long available, bool succeeds)
    {
        var item = AddItem(1, 0);
        _sender.Money = available;
        var result = Send(copper: 200, slots: [(SlotType.Inventory, (byte)0)]);
        await Assert.That(result == MailResult.Success).IsEqualTo(succeeds);
        await Assert.That(_sender.Money).IsEqualTo(succeeds ? 0L : available);
        await Assert.That(item.OwnerId).IsEqualTo(succeeds ? 2UL : 1UL);
        await Assert.That(_commitCalls).IsEqualTo(succeeds ? 1 : 0);
    }

    [Test]
    public async Task MoneyOnlyRepeat_IsASecondFullyPaidSend()
    {
        var first = Send(copper: 200);
        var second = Send(copper: 200);
        await Assert.That(first).IsEqualTo(MailResult.Success);
        await Assert.That(second).IsEqualTo(MailResult.Success);
        await Assert.That(_sender.Money).IsEqualTo(400L);
        await Assert.That(_mails._allPlayerMails.Values.Sum(mail => mail.Body.CopperCoins)).IsEqualTo(400);
        await Assert.That(_commitCalls).IsEqualTo(2);
    }

    [Test]
    public async Task RecipientFullBagAndLargeMailbox_UsesUnlimitedMailContainer()
    {
        _receiver.Inventory.Bag.ContainerSize = 0;
        for (var i = 1; i <= 1000; i++)
            _mails._allPlayerMails[i] = new BaseMail { Id = i, ReceiverName = "Receiver", Header = { ReceiverId = 2 } };
        var item = AddItem(1, 0);
        var result = Send(type: MailType.Normal, slots: [(SlotType.Inventory, (byte)0)]);
        await Assert.That(result).IsEqualTo(MailResult.Success);
        await Assert.That(_receiver.Inventory.MailAttachments.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(1001);
        await Assert.That(_mails._allPlayerMails[10000].IsDelivered).IsFalse();
        await Assert.That(_sender.Money).IsEqualTo(950L);
    }

    private MailResult Send(MailType type = MailType.Express, int copper = 0, int billing = 0,
        int other = 0, IReadOnlyList<(SlotType Type, byte Slot)> slots = null) =>
        PlayerMailSendExecutor.Execute(_sender, type, "Receiver", "Title", "Text", copper,
            billing, other, slots ?? [], _mails, _items, _names, () => { _commitCalls++; return _commit(); });

    private Item AddItem(ulong id, int slot)
    {
        var item = new Item { Id = id, TemplateId = 100, Template = new ItemTemplate { Id = 100, MaxCount = 100 },
            Count = 5, OwnerId = 1, SlotType = SlotType.Inventory, Slot = slot,
            _holdingContainer = _sender.Inventory.Bag };
        _allItems[id] = item;
        _sender.Inventory.Bag.Items.Add(item);
        _sender.Inventory.Bag.UpdateFreeSlotCount();
        return item;
    }

    private static CharacterMock Character(uint id, string name, Mock<ISession> session)
    {
        var character = new CharacterMock { Id = id, Name = name, Money = 1000,
            NumInventorySlots = 10, NumBankSlots = 10, Connection = new GameConnection(session.Object) };
        character.Mails = new CharacterMails(character);
        return character;
    }

    private static T SwapSingleton<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (T)field.GetValue(null);
        field.SetValue(null, instance);
        return previous;
    }

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class MailIds : IMailIdManager
    {
        private uint _next = 10000;
        public List<uint> Released { get; } = [];
        public bool Initialize(bool forceReset = false) => true;
        public void Load() { }
        public uint GetNextId() => _next++;
        public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => GetNextId()).ToArray();
        public void ReleaseId(uint usedObjectId) => Released.Add(usedObjectId);
        public void ReleaseId(IEnumerable<uint> usedObjectIds) => Released.AddRange(usedObjectIds);
    }

    private sealed class ThrowingSession : ISession
    {
        public System.Net.IPAddress Ip => System.Net.IPAddress.Loopback;
        public uint SessionId => 1;
        public System.Net.Sockets.Socket Socket => null;
        public void SendPacket(byte[] packet) => throw new IOException("Notification failed after commit");
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
