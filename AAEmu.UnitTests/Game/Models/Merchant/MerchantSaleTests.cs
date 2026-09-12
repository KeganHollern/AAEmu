using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Merchant;

[NotInParallel]
public sealed class MerchantSaleTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private Dictionary<ulong, Item> _allItems;
    private Dictionary<int, GradeTemplate> _grades;
    private List<ulong> _deleted;
    private CharacterMock _character;
    private RecordingContainer _bag;
    private RecordingContainer _equipment;
    private RecordingSession _session;
    private WorldInstance _world;
    private Npc _merchant;
    private WorldManager _worldManager;

    [Before(Test)]
    public void SetUp()
    {
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        SetInstance(items);
        SetInstance(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetInstance(_worldManager);
        _world = CreateWorld(1);
        _merchant = new Npc
        {
            ObjId = 77, Template = new NpcTemplate { Merchant = true }, ParentWorld = _world
        };
        _world.SetNpc(_merchant.ObjId, _merchant);
        _session = new RecordingSession();
        _character = new CharacterMock
        {
            Id = 7, ObjId = 7, Name = "Seller", Money = 100, NumInventorySlots = 255,
            NumBankSlots = 10, ParentWorld = _world, Connection = new GameConnection(_session)
        };
        _character.Connection.ActiveChar = _character;
        _character.CurrentInteractionObject = _merchant;
        _allItems = [];
        _deleted = [];
        _grades = new Dictionary<int, GradeTemplate> { [0] = new() { Grade = 0, RefundMultiplier = 100 } };
        SetField(items, "_allItems", _allItems);
        SetField(items, "_removedItems", _deleted);
        SetField(items, "_grades", _grades);
        var containers = new Dictionary<ulong, ItemContainer>();
        ulong id = 1;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;
            var container = new RecordingContainer(_character, slotType)
            {
                ContainerId = id++, ContainerSize = slotType == SlotType.Equipment ? 28 : -1
            };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _character.Inventory = new Inventory(_character);
        _bag = (RecordingContainer)_character.Inventory.Bag;
        _equipment = (RecordingContainer)_character.Inventory.Equipment;
        _character.BuyBackItems = new ItemContainer(_character.Id, SlotType.None, false, _character)
        {
            Owner = _character
        };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, instance) in _previousInstances)
            field.SetValue(null, instance);
        _previousInstances.Clear();
    }

    [Test]
    public async Task Packet_255CopiesOfOneSlot_RejectsWholeBatchWithoutPayment()
    {
        var item = AddItem(1, _bag, 0, count: 3);
        var request = Request(item);
        var stream = SendPacket(Enumerable.Repeat(request, 255).ToArray());

        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        await AssertUnchanged(item);
        await AssertSingleError(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    public async Task Packet_DuplicateItemIdAcrossContainers_RejectsBothEntries()
    {
        var first = AddItem(1, _bag, 0);
        var other = AddItem(2, _equipment, 0);
        SendPacket([Request(first), new MerchantSaleRequest(SlotType.Equipment, 0, first.Id)]);

        await AssertUnchanged(first, other);
        await AssertSingleError(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    public async Task Packet_ValidMixedStacks_PaysTruncatedPerUnitRefundOnceAndKeepsExactItems()
    {
        var first = AddItem(1, _bag, 0, count: 2, refund: 3);
        var second = AddItem(2, _bag, 1, refund: 7);
        first.Detail = [3, 4];
        _grades[0].RefundMultiplier = 150;
        var observations = new List<(long Money, int Bag, int Equipment, int Buyback, int Deleted)>();
        void Observe() => observations.Add((_character.Money, _bag.Items.Count, _equipment.Items.Count,
            _character.BuyBackItems.Items.Count, _deleted.Count));
        _session.OnPacket = Observe;
        _bag.OnLeave = Observe;
        _equipment.OnLeave = Observe;

        SendPacket([Request(first), Request(second)]);

        // floor(3 * 1.5) * 2 + floor(7 * 1.5) = 18, not floor(3 * 1.5 * 2) + 10.
        await Assert.That(_character.Money).IsEqualTo(118L);
        await Assert.That(observations.Count).IsEqualTo(3);
        await Assert.That(observations.All(value => value == (118L, 0, 0, 2, 2))).IsTrue();
        await Assert.That(_character.BuyBackItems.Items.SequenceEqual([second, first])).IsTrue();
        await Assert.That(first.Detail).IsEquivalentTo(new byte[] { 3, 4 });
        await Assert.That(_allItems[1]).IsSameReferenceAs(first);
        await Assert.That(_allItems[2]).IsSameReferenceAs(second);
        await Assert.That(_deleted).IsEquivalentTo([1UL, 2UL]);
        var body = new PacketStream(_session.Packets.Single()[8..]);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)ItemTaskType.StoreSell);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)3);
        ReadRemove(body, first.Id, SlotType.Inventory, 0);
        ReadRemove(body, second.Id, SlotType.Inventory, 1);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)ItemAction.ChangeMoneyAmount);
        await Assert.That(body.ReadInt32()).IsEqualTo(18);
    }

    [Test]
    [Arguments(0U)]
    [Arguments(1U)]
    [Arguments(uint.MaxValue)]
    public async Task Packet_UnconfirmedTrailingField_DoesNotControlAuthoritativeStackCount(uint trailing)
    {
        var item = AddItem(1, _bag, 0, count: 3, refund: 4);
        SendPacket([Request(item)], trailing: trailing);

        await Assert.That(_character.Money).IsEqualTo(112L);
        await Assert.That(item.Count).IsEqualTo(3);
        await Assert.That(item._holdingContainer).IsSameReferenceAs(_character.BuyBackItems);
    }

    [Test]
    [Arguments(3.01f)]
    [Arguments(1000f)]
    [Arguments(float.NaN)]
    public async Task Packet_RemoteMerchant_RejectsSale(float distance)
    {
        var item = AddItem(1, _bag, 0);
        _merchant.Transform.Local.SetPosition(distance, 0, 0);
        SendPacket([Request(item)]);

        await AssertUnchanged(item);
        await AssertSingleError(ErrorMessageType.TooFarAway);
    }

    [Test]
    public async Task MerchantAtThreeMetres_UsesExistingStoreRangeBoundary()
    {
        var item = AddItem(1, _bag, 0);
        _merchant.Transform.Local.SetPosition(3, 0, 0);
        var result = Sell(item);

        await Assert.That(result).IsEqualTo(MerchantSaleResult.Success);
        await Assert.That(_character.Money).IsEqualTo(110L);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("nonmerchant")]
    [Arguments("otherInstance")]
    [Arguments("despawned")]
    [Arguments("staleObjectId")]
    public async Task MerchantMustBeAuthoritativeInTheSameWorldInstance(string invalid)
    {
        var item = AddItem(1, _bag, 0);
        var npcId = _merchant.ObjId;
        switch (invalid)
        {
            case "missing": npcId = 999; break;
            case "nonmerchant": _merchant.Template.Merchant = false; break;
            case "otherInstance": _merchant.ParentWorld = CreateWorld(2); break;
            case "despawned": _merchant.Despawned = true; break;
            case "staleObjectId": _merchant.ObjId++; break;
        }
        var result = MerchantSaleExecutor.Execute(_character, npcId, [Request(item)]);

        await Assert.That(result).IsEqualTo(MerchantSaleResult.InvalidMerchant);
        await AssertUnchanged(item);
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    [Arguments("unsellable")]
    [Arguments("zeroCount")]
    [Arguments("overstack")]
    [Arguments("owner")]
    [Arguments("holdingContainer")]
    [Arguments("slotType")]
    [Arguments("registeredIdentity")]
    [Arguments("missingGrade")]
    [Arguments("negativeRefund")]
    [Arguments("negativeGradeRefund")]
    [Arguments("templateIdentity")]
    public async Task InvalidLaterEntry_PreservesTheEntireBatch(string invalid)
    {
        var first = AddItem(1, _bag, 0);
        var second = AddItem(2, _bag, 1);
        var requests = new[] { Request(first), Request(second) };
        switch (invalid)
        {
            case "unsellable": second.Template.Sellable = false; break;
            case "zeroCount": second.Count = 0; break;
            case "overstack": second.Count = second.Template.MaxCount + 1; break;
            case "owner": second.OwnerId++; break;
            case "holdingContainer": second._holdingContainer = _equipment; break;
            case "slotType": second.SlotType = SlotType.Bank; break;
            case "registeredIdentity": _allItems[second.Id] = new ItemMock(2); break;
            case "missingGrade": second.Grade = 9; break;
            case "negativeRefund": second.Template.Refund = -1; break;
            case "negativeGradeRefund": second.Grade = 1; _grades[1] = new GradeTemplate { RefundMultiplier = -1 }; break;
            case "templateIdentity": second.TemplateId++; break;
        }
        var result = MerchantSaleExecutor.Execute(_character, _merchant.ObjId, requests);

        await Assert.That(result).IsEqualTo(MerchantSaleResult.InvalidItem);
        await Assert.That(_bag.Items.SequenceEqual([first, second])).IsTrue();
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_character.BuyBackItems.Items).IsEmpty();
        await Assert.That(_deleted).IsEmpty();
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    public async Task Packet_StaleItemAtAReusedSlot_RejectsWithoutSellingTheReplacement()
    {
        var item = AddItem(2, _bag, 0);
        SendPacket([new MerchantSaleRequest(SlotType.Inventory, 0, 1)]);

        await AssertUnchanged(item);
        await AssertSingleError(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    [Arguments(SlotType.Bank)]
    [Arguments(SlotType.Mail)]
    [Arguments(SlotType.None)]
    public async Task UnsupportedSourceContainer_RejectsInsteadOfSkippingEntry(SlotType slotType)
    {
        var item = AddItem(1, _bag, 0);
        var result = MerchantSaleExecutor.Execute(_character, _merchant.ObjId,
            [new MerchantSaleRequest(slotType, 0, item.Id)]);

        await Assert.That(result).IsEqualTo(MerchantSaleResult.InvalidItem);
        await AssertUnchanged(item);
    }

    [Test]
    public async Task Packet_LaterMoveFails_RestoresEarlierMoveBeforeSendingError()
    {
        var first = AddItem(1, _bag, 0);
        var second = AddItem(2, _bag, 1);
        _character.BuyBackItems.ContainerSize = 1;
        var callbackCount = 0;
        _bag.OnLeave = () => callbackCount++;
        var restoredAtError = false;
        _session.OnPacket = () => restoredAtError = _bag.Items.SequenceEqual([first, second]) &&
            _character.Money == 100 && _character.BuyBackItems.Items.Count == 0 && _deleted.Count == 0;

        SendPacket([Request(first), Request(second)]);

        await Assert.That(restoredAtError).IsTrue();
        await Assert.That(callbackCount).IsEqualTo(0);
        await AssertUnchanged(first, second);
        await Assert.That(_bag.FreeSlotCount).IsEqualTo(253);
        await AssertSingleError(ErrorMessageType.StoreHaveProblem);
    }

    [Test]
    public async Task LaterMoveThrows_RestoresFirstMoveWithoutQueueingDatabaseDeletion()
    {
        var first = AddItem(1, _bag, 0);
        var second = AddItem(2, _bag, 1);
        _character.BuyBackItems = new ThrowingBuyback(_character, second.Id);
        var result = MerchantSaleExecutor.Execute(_character, _merchant.ObjId, [Request(first), Request(second)]);

        await Assert.That(result).IsEqualTo(MerchantSaleResult.Failed);
        await AssertUnchanged(first, second);
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    [Arguments("total")]
    [Arguments("product")]
    [Arguments("wallet")]
    public async Task Overflow_RejectsWithoutMovingOrPaying(string overflow)
    {
        var first = AddItem(1, _bag, 0, refund: int.MaxValue);
        var second = AddItem(2, _bag, 1, refund: 1);
        var originalMoney = _character.Money;
        MerchantSaleRequest[] requests;
        if (overflow == "product")
        {
            first.Template.MaxCount = int.MaxValue;
            first.Count = int.MaxValue;
            _grades[0].RefundMultiplier = int.MaxValue;
            requests = [Request(first)];
        }
        else if (overflow == "wallet")
        {
            originalMoney = _character.Money = long.MaxValue;
            requests = [Request(second)];
        }
        else
            requests = [Request(first), Request(second)];

        var result = MerchantSaleExecutor.Execute(_character, _merchant.ObjId, requests);

        await Assert.That(result != MerchantSaleResult.Success).IsTrue();
        await Assert.That(_character.Money).IsEqualTo(originalMoney);
        await Assert.That(_bag.Items.SequenceEqual([first, second])).IsTrue();
        await Assert.That(_character.BuyBackItems.Items).IsEmpty();
        await Assert.That(_deleted).IsEmpty();
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    public async Task MaximumRefund_FitsTheWalletAndWireDelta()
    {
        var item = AddItem(1, _bag, 0, refund: int.MaxValue);
        var result = Sell(item);

        await Assert.That(result).IsEqualTo(MerchantSaleResult.Success);
        await Assert.That(_character.Money).IsEqualTo(100L + int.MaxValue);
        await Assert.That(_deleted).IsEquivalentTo([1UL]);
    }

    [Test]
    [Arguments(int.MaxValue, 100, 1, int.MaxValue)]
    [Arguments(16_777_217, 100, 1, 16_777_217)]
    [Arguments(3, 150, 2, 8)]
    public async Task SaleBuybackSale_UsesOneCheckedPriceWithoutMintingCurrency(
        int unitRefund, int multiplier, int count, int expectedPrice)
    {
        var item = AddItem(1, _bag, 0, count, unitRefund);
        _grades[0].RefundMultiplier = multiplier;
        var sold = Sell(item);
        var moneyAfterSale = _character.Money;
        lock (SaveManager.PersistenceSyncRoot)
        {
            StorePurchaseExecutor.Execute(_character, new MerchantGoods(1), null,
                [item.Slot], remotePurchase: false, hasNpc: true);
        }

        await Assert.That(sold).IsEqualTo(MerchantSaleResult.Success);
        await Assert.That(moneyAfterSale).IsEqualTo(100L + expectedPrice);
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item.IsDirty).IsTrue();
        await Assert.That(_character.BuyBackItems.Items).IsEmpty();

        var soldAgain = Sell(item);

        await Assert.That(soldAgain).IsEqualTo(MerchantSaleResult.Success);
        await Assert.That(_character.Money).IsEqualTo(100L + expectedPrice);
        await Assert.That(_character.BuyBackItems.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_deleted).IsEquivalentTo([1UL]);
    }

    [Test]
    public async Task SuccessfulSaleReplay_PaysOnlyOnce()
    {
        var item = AddItem(1, _bag, 0, count: 3);
        var request = Request(item);
        SendPacket([request]);
        SendPacket([request]);

        await Assert.That(_character.Money).IsEqualTo(130L);
        await Assert.That(_character.BuyBackItems.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_deleted).IsEquivalentTo([1UL]);
        await Assert.That(_session.Packets.Count).IsEqualTo(2);
        await Assert.That(BitConverter.ToUInt16(_session.Packets[1], 6)).IsEqualTo(SCOffsets.SCErrorMsgPacket);
    }

    [Test]
    public async Task ConcurrentCopiesOfTheSameBatch_ProduceExactlyOneSale()
    {
        var first = AddItem(1, _bag, 0);
        var second = AddItem(2, _bag, 1, count: 2);
        MerchantSaleRequest[] requests = [Request(first), Request(second)];
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return MerchantSaleExecutor.Execute(_character, _merchant.ObjId, requests);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(results.Count(result => result == MerchantSaleResult.Success)).IsEqualTo(1);
        await Assert.That(_character.Money).IsEqualTo(130L);
        await Assert.That(_bag.Items).IsEmpty();
        await Assert.That(_character.BuyBackItems.Items.Count).IsEqualTo(2);
        await Assert.That(_deleted).IsEquivalentTo([1UL, 2UL]);
        await Assert.That(_session.Packets).HasSingleItem();
    }

    [Test]
    public async Task Packet_255DistinctSales_SplitsNotificationsWithoutTruncatingAnyTask()
    {
        var items = Enumerable.Range(0, 255).Select(slot => AddItem((uint)slot + 1, _bag, (byte)slot)).ToArray();
        SendPacket(items.Select(Request).ToArray());

        await Assert.That(_character.Money).IsEqualTo(2650L);
        await Assert.That(_character.BuyBackItems.Items.Count).IsEqualTo(255);
        await Assert.That(_deleted.Count).IsEqualTo(255);
        await Assert.That(_session.Packets.Count).IsEqualTo(9);
        await Assert.That(_session.Packets.Sum(packet => (int)packet[9])).IsEqualTo(256);
        await Assert.That(_session.Packets.All(packet => packet[9] <= 30)).IsTrue();
    }

    [Test]
    public async Task Sale_NoOpenInteraction_LeavesItemAndMoneyUntouched()
    {
        var item = AddItem(1, _bag, 0);
        _character.CurrentInteractionObject = null;
        SendPacket([Request(item)]);
        await AssertSingleError(ErrorMessageType.TooFarAway);
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
    }

    [Test]
    public async Task Sale_EquippedItem_RejectsTheEntireBatch()
    {
        var bag = AddItem(1, _bag, 0);
        var equipped = AddItem(2, _equipment, 0);
        SendPacket([Request(bag), Request(equipped)]);
        await AssertSingleError(ErrorMessageType.StoreInvalidItem);
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(bag);
        await Assert.That(_equipment.Items.Single()).IsSameReferenceAs(equipped);
    }

    [Test]
    public async Task Sale_MerchantOnAnotherFloor_RejectsOutsideThreeDimensionalRange()
    {
        var item = AddItem(1, _bag, 0);
        _merchant.Transform.Local.SetPosition(0, 0, 3.01f);
        SendPacket([Request(item)]);
        await AssertSingleError(ErrorMessageType.TooFarAway);
        await Assert.That(_character.Money).IsEqualTo(100L);
    }

    private MerchantSaleResult Sell(Item item) =>
        MerchantSaleExecutor.Execute(_character, _merchant.ObjId, [Request(item)]);

    private PacketStream SendPacket(MerchantSaleRequest[] requests, uint trailing = 0)
    {
        var body = new PacketStream().WriteBc(_merchant.ObjId).WriteBc(0U).Write((byte)requests.Length);
        foreach (var request in requests)
            body.Write((byte)request.SlotType).Write(request.Slot).Write(request.ItemId).Write(trailing);
        var read = new PacketStream(body.GetBytes());
        new CSSellItemsPacket { Connection = _character.Connection }.Read(read);
        return read;
    }

    private async Task AssertUnchanged(params Item[] items)
    {
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_character.BuyBackItems.Items).IsEmpty();
        await Assert.That(_deleted).IsEmpty();
        foreach (var item in items)
        {
            await Assert.That(item._holdingContainer).IsSameReferenceAs(
                item.SlotType == SlotType.Equipment ? _equipment : _bag);
            await Assert.That(item._holdingContainer.Items.Contains(item)).IsTrue();
            await Assert.That(item.IsDirty).IsFalse();
        }
    }

    private async Task AssertSingleError(ErrorMessageType expected)
    {
        await Assert.That(_session.Packets).HasSingleItem();
        await Assert.That(BitConverter.ToUInt16(_session.Packets[0], 6)).IsEqualTo(SCOffsets.SCErrorMsgPacket);
        await Assert.That(BitConverter.ToInt16(_session.Packets[0], 8)).IsEqualTo((short)expected);
    }

    private static void ReadRemove(PacketStream body, ulong id, SlotType slotType, byte slot)
    {
        if (body.ReadByte() != (byte)ItemAction.Seize || body.ReadByte() != (byte)slotType ||
            body.ReadByte() != slot || body.ReadUInt64() != id)
            throw new InvalidOperationException("Incorrect authoritative remove task.");
    }

    private ItemMock AddItem(uint id, ItemContainer container, byte slot, int count = 1, int refund = 10)
    {
        var item = new ItemMock(id, new ItemTemplate
        {
            Id = id + 100, Refund = refund, MaxCount = 100, Sellable = true
        }, count)
        {
            OwnerId = _character.Id, SlotType = container.ContainerType, Slot = slot,
            _holdingContainer = container, IsDirty = false
        };
        container.Items.Add(item);
        container.UpdateFreeSlotCount();
        container.IsDirty = false;
        _allItems.Add(id, item);
        return item;
    }

    private static MerchantSaleRequest Request(Item item) => new(item.SlotType, (byte)item.Slot, item.Id);

    private WorldInstance CreateWorld(uint id)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, id);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worldManager)!;
        worlds[id] = world;
        return world;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingContainer(Character owner, SlotType slotType)
        : ItemContainer(owner.Id, slotType, false, owner)
    {
        public Action OnLeave { get; set; }
        public override void OnLeaveContainer(Item item, ItemContainer destination, byte previousSlot) => OnLeave?.Invoke();
    }

    private sealed class ThrowingBuyback(Character owner, ulong rejectId)
        : ItemContainer(owner.Id, SlotType.None, false, owner)
    {
        public override bool CanAccept(Item item, int targetSlot) =>
            item.Id == rejectId ? throw new InvalidOperationException("Forced second move failure") : true;
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public Action OnPacket { get; set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { Packets.Add(packet.ToArray()); OnPacket?.Invoke(); }
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
