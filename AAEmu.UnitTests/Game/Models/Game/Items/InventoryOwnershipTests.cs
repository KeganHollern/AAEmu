using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class InventoryOwnershipTests
{
    private readonly Dictionary<FieldInfo, object> _previousSingletons = [];
    private Dictionary<ulong, Item> _items;
    private Dictionary<ulong, ItemContainer> _containers;
    private CharacterMock _owner;
    private CharacterMock _other;
    private WorldInstance _world;
    private CofferContainer _coffer;

    [Before(Test)]
    public void SetUp()
    {
        var worldManager = new WorldManager(null, null, null, null, null);
        SetSingleton(worldManager);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        _world.MateManager = new MateManager(_world);
        ((ConcurrentDictionary<uint, WorldInstance>)GetField(worldManager, "_worlds"))[1] = _world;
        var ids = Mock.Of<IItemIdManager>();
        ids.GetNextId().Returns(1000U);
        var itemManager = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, worldManager);
        SetSingleton(itemManager);
        SetSingleton(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _items = [];
        _containers = [];
        SetField(itemManager, "_allItems", _items);
        SetField(itemManager, "_removedItems", new List<ulong>());
        SetField(itemManager, "_allPersistentContainers", _containers);
        _owner = AddOwner(7);
        _other = AddOwner(8);
        _coffer = new CofferContainer(_other.Id, false)
            { ContainerId = 999, ContainerSize = 10, Owner = _other };
        _containers.Add(_coffer.ContainerId, _coffer);
        _world.AddObject(new OpenCoffer
            { ObjId = 99, ParentWorld = _world, OpenedBy = _owner, ItemContainer = _coffer });
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousSingletons)
            field.SetValue(null, previous);
        _previousSingletons.Clear();
    }

    [Test]
    public async Task Split_PreservesDetailsAndOwner_ThenTheNewStackCanMove()
    {
        var source = AddItem(1, _owner.Inventory.Bag, 10);
        source.Grade = 3;
        source.MadeUnitId = 44;
        source.ItemFlags = ItemFlag.Unpacked;
        source.UnpackTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        source.ExpirationTime = source.UnpackTime.AddDays(1);
        source.Detail = [1, 2, 3];

        await Assert.That(_owner.Inventory.SplitOrMoveItem(ItemTaskType.Invalid, source.Id,
            SlotType.Inventory, 0, 0, SlotType.Inventory, 1, 3)).IsTrue();
        var split = _items[1000];
        await Assert.That(source.Count).IsEqualTo(7);
        await Assert.That(split.Count).IsEqualTo(3);
        await Assert.That(split.OwnerId).IsEqualTo(_owner.Id);
        await Assert.That(split.Grade).IsEqualTo(source.Grade);
        await Assert.That(split.MadeUnitId).IsEqualTo(source.MadeUnitId);
        await Assert.That(split.ItemFlags).IsEqualTo(source.ItemFlags);
        await Assert.That(split.UnpackTime).IsEqualTo(source.UnpackTime);
        await Assert.That(split.ExpirationTime).IsEqualTo(source.ExpirationTime);
        await Assert.That(split.Detail.SequenceEqual(source.Detail)).IsTrue();
        await Assert.That(ReferenceEquals(split.Detail, source.Detail)).IsFalse();
        await Assert.That(_owner.Inventory.GetItemById(split.Id)).IsSameReferenceAs(split);
        await Assert.That(_owner.Inventory.SplitOrMoveItem(ItemTaskType.Invalid, split.Id,
            SlotType.Inventory, 1, 0, SlotType.Inventory, 2, 3)).IsTrue();
        await Assert.That(split.Slot).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Coffer_ForeignSourceOrTargetId_DoesNotChangeEitherInventory(bool foreignTarget)
    {
        var foreign = AddItem(1, _other.Inventory.Bag, 5);
        var held = AddItem(2, _coffer, 5);
        var result = foreignTarget
            ? _owner.Inventory.SwapCofferItems(held.Id, foreign.Id, SlotType.Trade, 0,
                SlotType.Inventory, 0, _coffer.ContainerId)
            : _owner.Inventory.SwapCofferItems(foreign.Id, held.Id, SlotType.Inventory, 0,
                SlotType.Trade, 0, _coffer.ContainerId);

        await Assert.That(result).IsFalse();
        await Assert.That(foreign.Count).IsEqualTo(5);
        await Assert.That(held.Count).IsEqualTo(5);
        await Assert.That(foreign._holdingContainer).IsSameReferenceAs(_other.Inventory.Bag);
        await Assert.That(held._holdingContainer).IsSameReferenceAs(_coffer);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
    }

    [Test]
    [Arguments(SlotType.Bank, false)]
    [Arguments(SlotType.Bank, true)]
    [Arguments(SlotType.Equipment, false)]
    [Arguments(SlotType.Mail, true)]
    public async Task OpenCoffer_CannotMoveItemsThroughAnotherPlayerContainer(SlotType endpoint, bool split)
    {
        var container = _containers.Values.Single(item => item.OwnerId == _owner.Id && item.ContainerType == endpoint);
        var item = AddItem(1, container, 5);
        var result = split
            ? _owner.Inventory.SplitCofferItems(2, item.Id, 0, endpoint, 0,
                SlotType.Trade, 0, _coffer.ContainerId)
            : _owner.Inventory.SwapCofferItems(item.Id, 0, endpoint, 0,
                SlotType.Trade, 0, _coffer.ContainerId);
        await Assert.That(result).IsFalse();
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(item._holdingContainer).IsSameReferenceAs(container);
        await Assert.That(_coffer.Items).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SharedCoffer_TransferAndSplit_UseTheFinalContainerOwner(bool split)
    {
        var item = AddItem(1, _owner.Inventory.Bag, 5);
        var result = split
            ? _owner.Inventory.SplitCofferItems(2, item.Id, 0, SlotType.Inventory, 0,
                SlotType.Trade, 0, _coffer.ContainerId)
            : _owner.Inventory.SwapCofferItems(item.Id, 0, SlotType.Inventory, 0,
                SlotType.Trade, 0, _coffer.ContainerId);
        await Assert.That(result).IsTrue();
        var moved = _coffer.Items.Single();
        await Assert.That(moved.OwnerId).IsEqualTo(_coffer.OwnerId);
        await Assert.That(moved._holdingContainer).IsSameReferenceAs(_coffer);
        var returnSlot = split ? (byte)1 : (byte)0;
        await Assert.That(_owner.Inventory.SwapCofferItems(moved.Id, 0, SlotType.Trade, 0,
            SlotType.Inventory, returnSlot, _coffer.ContainerId)).IsTrue();
        await Assert.That(moved.OwnerId).IsEqualTo(_owner.Id);
        await Assert.That(_owner.Inventory.GetItemById(moved.Id)).IsSameReferenceAs(moved);
        await Assert.That(_owner.Inventory.Bag.Items.Sum(held => held.Count)).IsEqualTo(5);
        await Assert.That(_coffer.Items).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MateEquipment_RequestUsesTheActiveCharacterOwner(bool owned)
    {
        var mateOwner = owned ? _owner : _other;
        var mate = new Mate { TlId = 12, ParentWorld = _world };
        mate.Equipment = new ItemContainer(mateOwner.Id, SlotType.EquipmentMate, false, mate)
            { Owner = mateOwner };
        ((Dictionary<uint, List<Mate>>)GetField(_world.MateManager, "_activeMates"))[mateOwner.Id] = [mate];
        var template = new EquipItemTemplate { Id = 200, MaxCount = 1, BindType = ItemBindType.Normal };
        var gear = new EquipItem(1, template, 1)
            { OwnerId = mateOwner.Id, SlotType = SlotType.EquipmentMate, Slot = 0, _holdingContainer = mate.Equipment };
        mate.Equipment.Items.Add(gear);
        _items.Add(gear.Id, gear);
        var wire = new PacketStream().Write(_owner.Id).Write((ushort)12).Write(0U).Write(false).Write((byte)1);
        wire.Write(0U);
        gear.Write(wire);
        wire.Write((byte)SlotType.Inventory).Write((byte)0).Write((byte)SlotType.EquipmentMate).Write((byte)0);

        new CSChangeMateEquipmentPacket { Connection = _owner.Connection }.Read(wire);

        await Assert.That(_owner.Inventory.Bag.Items.Contains(gear)).IsEqualTo(owned);
        await Assert.That(mate.Equipment.Items.Contains(gear)).IsEqualTo(!owned);
        await Assert.That(gear.OwnerId).IsEqualTo(mateOwner.Id);
        await Assert.That(gear._holdingContainer).IsSameReferenceAs(owned ? _owner.Inventory.Bag : mate.Equipment);
    }

    private CharacterMock AddOwner(uint id)
    {
        var connection = new GameConnection(Mock.Of<ISession>().Object);
        var owner = new CharacterMock
            { Id = id, ObjId = id * 10, ParentWorld = _world, NumInventorySlots = 10, NumBankSlots = 10, Connection = connection };
        connection.ActiveChar = owner;
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(id, type, false, owner)
                { ContainerId = (ulong)_containers.Count + 1, Owner = owner };
            _containers.Add(container.ContainerId, container);
        }
        owner.Inventory = new Inventory(owner);
        return owner;
    }

    private Item AddItem(ulong id, ItemContainer container, int count)
    {
        var item = new Item(id, new ItemTemplate { Id = 100, MaxCount = 100, BindType = ItemBindType.Normal }, count)
            { OwnerId = container.OwnerId, SlotType = container.ContainerType, Slot = container.Items.Count,
                _holdingContainer = container };
        container.Items.Add(item);
        container.UpdateFreeSlotCount();
        _items.Add(id, item);
        return item;
    }

    private void SetSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _previousSingletons.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static object GetField(object value, string name) =>
        value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value);

    private static void SetField(object value, string name, object fieldValue) =>
        value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, fieldValue);

    private sealed class OpenCoffer : DoodadCoffer
    {
        public override bool AllowedToInteract(Character character) => ReferenceEquals(character, OpenedBy);
    }
}
