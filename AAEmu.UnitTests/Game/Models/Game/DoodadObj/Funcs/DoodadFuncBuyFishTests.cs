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
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj.Funcs;

[NotInParallel]
public sealed class DoodadFuncBuyFishTests
{
    private const uint FishPackTemplateId = 27457;
    private const int FishPackRefund = 150000;
    private static readonly FieldInfo s_itemManagerInstance = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_questManagerInstance = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private ItemManager _previousItems;
    private QuestManager _previousQuests;
    private Dictionary<ulong, Item> _allItems;
    private List<ulong> _removedItems;
    private CharacterMock _character;
    private RecordingSession _session;
    private RecordingEquipment _equipment;

    public static IEnumerable<(uint FunctionId, uint DoodadId, uint SkillId)> BuyFishRows() =>
    [
        (1, 6507, 21904),
        (4, 7133, 21904),
        (6, 1713, 13789)
    ];

    [Before(Test)]
    public void SetUp()
    {
        _previousItems = (ItemManager)s_itemManagerInstance.GetValue(null);
        _previousQuests = (QuestManager)s_questManagerInstance.GetValue(null);
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        s_itemManagerInstance.SetValue(null, items);
        s_questManagerInstance.SetValue(null,
            new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        _removedItems = [];
        SetField(items, "_allItems", _allItems);
        SetField(items, "_removedItems", _removedItems);
        _session = new RecordingSession();
        _character = new CharacterMock
        {
            Id = 7,
            Money = 123,
            NumInventorySlots = 10,
            NumBankSlots = 10,
            Connection = new GameConnection(_session)
        };
        _equipment = new RecordingEquipment(_character) { ContainerId = 1, ContainerSize = 28 };
        var containers = new Dictionary<ulong, ItemContainer> { [1] = _equipment };
        ulong containerId = 2;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType is SlotType.EquipmentMate or SlotType.Equipment)
                continue;
            var container = new ItemContainer(_character.Id, slotType, false, _character)
            {
                ContainerId = containerId++, Owner = _character
            };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _character.Inventory = new Inventory(_character);
    }

    [After(Test)]
    public void TearDown()
    {
        s_itemManagerInstance.SetValue(null, _previousItems);
        s_questManagerInstance.SetValue(null, _previousQuests);
    }

    [Test]
    [MethodDataSource(nameof(BuyFishRows))]
    public async Task Use_DeployedFunction_ExchangesOnePackForOneRefund(uint functionId, uint doodadId, uint skillId)
    {
        var pack = EquipFish();
        var function = new DoodadFuncBuyFish { Id = functionId };
        var owner = new Doodad { TemplateId = doodadId };
        var observations = new List<(long Money, bool PackPresent)>();
        _character.Events.OnItemGather += (_, _) => observations.Add((_character.Money, _equipment.Items.Contains(pack)));
        _equipment.Leaving = _ => observations.Add((_character.Money, _equipment.Items.Contains(pack)));

        function.Use(_character, owner, skillId);

        var removedSlots = new List<(SlotType Container, byte Slot, ulong ItemId)>();
        await Assert.That(_character.Money).IsEqualTo(123L + FishPackRefund);
        await Assert.That(MoneyChanges(removedSlots)).IsEquivalentTo([FishPackRefund]);
        await Assert.That(removedSlots).IsEquivalentTo([
            (SlotType.Equipment, (byte)EquipmentItemSlot.Backpack, pack.Id)]);
        await Assert.That(_equipment.Items).IsEmpty();
        await Assert.That(pack._holdingContainer).IsNull();
        await Assert.That(_removedItems).IsEquivalentTo([pack.Id]);
        await Assert.That(_allItems.ContainsKey(pack.Id)).IsFalse();
        await Assert.That(owner.ItemTemplateId).IsEqualTo(FishPackTemplateId);
        await Assert.That(observations).IsEquivalentTo([
            (123L + FishPackRefund, false), (123L + FishPackRefund, false)]);
    }

    [Test]
    [MethodDataSource(nameof(BuyFishRows))]
    public async Task Use_DeployedFunction_ExactWalletCapacitySucceeds(uint functionId, uint doodadId, uint skillId)
    {
        var pack = EquipFish();
        _character.Money = long.MaxValue - FishPackRefund;

        new DoodadFuncBuyFish { Id = functionId }.Use(_character, new Doodad { TemplateId = doodadId }, skillId);

        await Assert.That(_character.Money).IsEqualTo(long.MaxValue);
        await Assert.That(MoneyChanges()).IsEquivalentTo([FishPackRefund]);
        await Assert.That(_equipment.Items).IsEmpty();
        await Assert.That(_removedItems).IsEquivalentTo([pack.Id]);
    }

    [Test]
    [MethodDataSource(nameof(BuyFishRows))]
    public async Task Use_DeployedFunction_OverflowPreservesTheExactPackAndWallet(uint functionId, uint doodadId, uint skillId)
    {
        var pack = EquipFish();
        var startingMoney = long.MaxValue - FishPackRefund + 1;
        _character.Money = startingMoney;
        var owner = new Doodad { TemplateId = doodadId, ItemTemplateId = 999 };
        var notifications = 0;
        var failureObservations = new List<(long Money, bool PackPresent)>();
        _character.Events.OnItemGather += (_, _) => notifications++;
        _equipment.Leaving = _ => notifications++;
        _session.OnSend = packet =>
        {
            if (Opcode(packet) == SCOffsets.SCErrorMsgPacket)
                failureObservations.Add((_character.Money, _equipment.Items.Contains(pack)));
        };

        new DoodadFuncBuyFish { Id = functionId }.Use(_character, owner, skillId);

        await AssertPackUnchanged(pack);
        await Assert.That(_character.Money).IsEqualTo(startingMoney);
        await Assert.That(MoneyChanges()).IsEmpty();
        await Assert.That(owner.ItemTemplateId).IsEqualTo(999U);
        await Assert.That(notifications).IsEqualTo(0);
        await Assert.That(failureObservations.Count).IsGreaterThan(0);
        await Assert.That(failureObservations.All(observation => observation == (startingMoney, true))).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(BuyFishRows))]
    public async Task Use_DeployedFunction_RepeatedRequestDoesNotPayForTheRemovedPack(uint functionId, uint doodadId, uint skillId)
    {
        var pack = EquipFish();
        var function = new DoodadFuncBuyFish { Id = functionId };
        var owner = new Doodad { TemplateId = doodadId };

        function.Use(_character, owner, skillId);
        function.Use(_character, owner, skillId);

        await Assert.That(_character.Money).IsEqualTo(123L + FishPackRefund);
        await Assert.That(MoneyChanges()).IsEquivalentTo([FishPackRefund]);
        await Assert.That(_removedItems).IsEquivalentTo([pack.Id]);
    }

    [Test]
    public async Task Use_NegativeRefund_PreservesPackAndBalance()
    {
        var pack = EquipFish(-1);
        var owner = new Doodad { ItemTemplateId = 999 };

        new DoodadFuncBuyFish { Id = 1 }.Use(_character, owner, 21904);

        await AssertPackUnchanged(pack);
        await Assert.That(_character.Money).IsEqualTo(123L);
        await Assert.That(MoneyChanges()).IsEmpty();
        await Assert.That(owner.ItemTemplateId).IsEqualTo(999U);
    }

    [Test]
    public async Task Use_Overflow_PreservesThePackExpirationAndPublishesNoLifespanChange()
    {
        var pack = EquipFish();
        var expiration = DateTime.UtcNow.AddHours(3);
        pack.ExpirationTime = expiration;
        pack.ExpirationOnlineMinutesLeft = 90;
        pack.IsDirty = false;
        _character.Money = long.MaxValue;

        new DoodadFuncBuyFish { Id = 1 }.Use(_character, new Doodad(), 21904);

        await AssertPackUnchanged(pack);
        await Assert.That(_character.Money).IsEqualTo(long.MaxValue);
        await Assert.That(pack.ExpirationTime).IsEqualTo(expiration);
        await Assert.That(pack.ExpirationOnlineMinutesLeft).IsEqualTo(90.0);
        await Assert.That(_session.Packets.Any(packet => Opcode(packet) == SCOffsets.SCSyncItemLifespanPacket)).IsFalse();
    }

    [Test]
    public async Task Use_NonDestroyablePack_DoesNotCreditOrPublishConsumption()
    {
        var pack = EquipFish(canDestroy: false);
        var notifications = 0;
        _character.Events.OnItemGather += (_, _) => notifications++;

        new DoodadFuncBuyFish { Id = 1 }.Use(_character, new Doodad(), 21904);

        await AssertPackUnchanged(pack);
        await Assert.That(_character.Money).IsEqualTo(123L);
        await Assert.That(MoneyChanges()).IsEmpty();
        await Assert.That(notifications).IsEqualTo(0);
    }

    [Test]
    public async Task Use_ConsumptionCallback_SeesCommittedPaymentAndCannotSellThePackAgain()
    {
        var pack = EquipFish();
        var function = new DoodadFuncBuyFish { Id = 1 };
        var owner = new Doodad();
        var observedMoney = 0L;
        var observedPack = true;
        _character.Events.OnItemGather += (_, _) =>
        {
            observedMoney = _character.Money;
            observedPack = _equipment.Items.Contains(pack);
            function.Use(_character, owner, 21904);
        };

        function.Use(_character, owner, 21904);

        await Assert.That(observedMoney).IsEqualTo(123L + FishPackRefund);
        await Assert.That(observedPack).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(123L + FishPackRefund);
        await Assert.That(MoneyChanges()).IsEquivalentTo([FishPackRefund]);
        await Assert.That(_removedItems).IsEquivalentTo([pack.Id]);
    }

    [Test]
    public async Task Use_SuccessNotifications_SeeTheCompleteExchange()
    {
        var pack = EquipFish();
        var observations = new List<(long Money, bool PackPresent)>();
        _session.OnSend = packet =>
        {
            if (Opcode(packet) == SCOffsets.SCItemTaskSuccessPacket)
                observations.Add((_character.Money, _equipment.Items.Contains(pack)));
        };

        new DoodadFuncBuyFish { Id = 1 }.Use(_character, new Doodad(), 21904);

        await Assert.That(observations.Count).IsGreaterThan(0);
        await Assert.That(observations.All(observation => observation == (123L + FishPackRefund, false))).IsTrue();
        await Assert.That(MoneyChanges()).IsEquivalentTo([FishPackRefund]);
    }

    [Test]
    public async Task Use_ConcurrentRequests_ExchangeTheEquippedPackOnce()
    {
        var pack = EquipFish();
        var function = new DoodadFuncBuyFish { Id = 1 };
        var owner = new Doodad();
        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            function.Use(_character, owner, 21904);
        })).ToArray();
        start.Set();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(_character.Money).IsEqualTo(123L + FishPackRefund);
        await Assert.That(MoneyChanges()).IsEquivalentTo([FishPackRefund]);
        await Assert.That(_equipment.Items).IsEmpty();
        await Assert.That(_removedItems).IsEquivalentTo([pack.Id]);
    }

    [Test]
    public async Task Use_NoEquippedPack_ChangesNothing()
    {
        new DoodadFuncBuyFish { Id = 1 }.Use(_character, new Doodad(), 21904);

        await Assert.That(_character.Money).IsEqualTo(123L);
        await Assert.That(MoneyChanges()).IsEmpty();
        await Assert.That(_removedItems).IsEmpty();
    }

    private TestBackpack EquipFish(int refund = FishPackRefund, bool canDestroy = true)
    {
        var pack = new TestBackpack(1234, new BackpackTemplate
        {
            Id = FishPackTemplateId,
            MaxCount = 1,
            Refund = refund,
            BindType = ItemBindType.Normal
        }, canDestroy)
        {
            OwnerId = _character.Id,
            SlotType = SlotType.Equipment,
            Slot = (int)EquipmentItemSlot.Backpack,
            _holdingContainer = _equipment,
            UccId = 4321,
            Detail = [1, 2, 3, 4],
            IsDirty = false
        };
        _allItems.Add(pack.Id, pack);
        _equipment.Items.Add(pack);
        _equipment.UpdateFreeSlotCount();
        return pack;
    }

    private async Task AssertPackUnchanged(Backpack pack)
    {
        await Assert.That(_equipment.Items).HasSingleItem();
        await Assert.That(_equipment.Items[0]).IsSameReferenceAs(pack);
        await Assert.That(pack._holdingContainer).IsSameReferenceAs(_equipment);
        await Assert.That(pack.SlotType).IsEqualTo(SlotType.Equipment);
        await Assert.That(pack.Slot).IsEqualTo((int)EquipmentItemSlot.Backpack);
        await Assert.That(pack.OwnerId).IsEqualTo((ulong)_character.Id);
        await Assert.That(pack.Count).IsEqualTo(1);
        await Assert.That(pack.UccId).IsEqualTo(4321UL);
        await Assert.That(pack.Detail).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(pack.IsDirty).IsFalse();
        await Assert.That(_allItems[pack.Id]).IsSameReferenceAs(pack);
        await Assert.That(_removedItems).IsEmpty();
    }

    private int[] MoneyChanges(List<(SlotType Container, byte Slot, ulong ItemId)> removedSlots = null)
    {
        var changes = new List<int>();
        foreach (var packet in _session.Packets.Where(packet => Opcode(packet) == SCOffsets.SCItemTaskSuccessPacket))
        {
            var stream = new PacketStream(packet[8..]);
            stream.ReadByte(); // The exchange may group its currency and removal tasks.
            var count = stream.ReadByte();
            for (var index = 0; index < count; index++)
            {
                switch ((ItemAction)stream.ReadByte())
                {
                    case ItemAction.ChangeMoneyAmount:
                        changes.Add(stream.ReadInt32());
                        break;
                    case ItemAction.Seize:
                        var container = (SlotType)stream.ReadByte();
                        var slot = stream.ReadByte();
                        var itemId = stream.ReadUInt64();
                        removedSlots?.Add((container, slot, itemId));
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected task in the fish-for-currency exchange.");
                }
            }
            var removedCount = stream.ReadByte();
            for (var index = 0; index < removedCount; index++)
                stream.ReadUInt64();
            if (stream.ReadUInt32() != 0 || stream.LeftBytes != 0)
                throw new InvalidOperationException("Unexpected trailing fish sale data.");
        }
        return changes.ToArray();
    }

    private static ushort Opcode(byte[] packet)
    {
        return BitConverter.ToUInt16(packet, 6);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class TestBackpack(ulong id, ItemTemplate template, bool canDestroy) : Backpack(id, template, 1)
    {
        public override bool CanDestroy()
        {
            return canDestroy;
        }
    }

    private sealed class RecordingEquipment(Character owner) : ItemContainer(owner.Id, SlotType.Equipment, false, owner)
    {
        public Action<Item> Leaving { get; set; }

        public override void OnLeaveContainer(Item item, ItemContainer newContainer, byte previousSlot)
        {
            Leaving?.Invoke(item);
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public Action<byte[]> OnSend { get; set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            Packets.Enqueue(packet.ToArray());
            OnSend?.Invoke(packet);
        }

        public void AddAttribute(string name, object attribute)
        {
            _attributes.Add(name, attribute);
        }

        public object GetAttribute(string name)
        {
            return _attributes.GetValueOrDefault(name);
        }

        public void ClearAttribute(string name)
        {
            _attributes.Remove(name);
        }

        public void Close()
        {
        }
    }
}
