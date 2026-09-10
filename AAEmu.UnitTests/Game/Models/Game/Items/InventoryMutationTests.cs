using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class InventoryMutationTests
{
    private static readonly FieldInfo s_itemManagerInstance = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_questManagerInstance = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private ItemManager _previousItems;
    private QuestManager _previousQuests;
    private Dictionary<ulong, Item> _allItems;
    private List<ulong> _deleted;
    private Dictionary<uint, ItemTemplate> _templates;
    private CharacterMock _owner;
    private ItemContainer _bag;

    [Before(Test)]
    public void SetUp()
    {
        _previousItems = (ItemManager)s_itemManagerInstance.GetValue(null);
        _previousQuests = (QuestManager)s_questManagerInstance.GetValue(null);
        var ids = Mock.Of<IItemIdManager>();
        // Repeated allocation lets tests force failure on a later new stack.
        ids.GetNextId().Returns(1_000U);
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        s_itemManagerInstance.SetValue(null, items);
        s_questManagerInstance.SetValue(null,
            new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        _deleted = [];
        _templates = [];
        SetField(items, "_allItems", _allItems);
        SetField(items, "_removedItems", _deleted);
        SetField(items, "_templates", _templates);
        _owner = new CharacterMock { Id = 7, Money = 100, NumInventorySlots = 10, NumBankSlots = 10 };
        var containers = new Dictionary<ulong, ItemContainer>();
        ulong containerId = 1;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_owner.Id, slotType, false, _owner)
                { ContainerId = containerId++, Owner = _owner };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _bag = _owner.Inventory.Bag;
    }

    [After(Test)]
    public void TearDown()
    {
        s_itemManagerInstance.SetValue(null, _previousItems);
        s_questManagerInstance.SetValue(null, _previousQuests);
    }

    [Test]
    public async Task LaterWalletFailure_RestoresExactRemovedItemAndDirtyStateWithoutEvents()
    {
        var item = AddItem(1, 100, 3);
        var expiration = DateTime.UtcNow.AddDays(1);
        item.ExpirationTime = expiration;
        item.ExpirationOnlineMinutesLeft = 30;
        item.IsDirty = false;
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryConsume(_bag, item, 3);
            result = mutation.TryChangeMoney(_owner, -101);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(3);
        await Assert.That(item.IsDirty).IsFalse();
        await Assert.That(item.ExpirationTime).IsEqualTo(expiration);
        await Assert.That(item.ExpirationOnlineMinutesLeft).IsEqualTo(30.0);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_allItems[1]).IsSameReferenceAs(item);
        await Assert.That(_deleted).IsEmpty();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task UncertainCommit_RetainsPreparedMoneyAndRemovalWithoutCallbacksOrIdReuse()
    {
        var item = AddItem(1, 100, 1);
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryConsume(_bag, item, 1);
            mutation.TryChangeMoney(_owner, -50);
            mutation.PreservePreparedState();
            mutation.PreservePreparedState();
        }
        await Assert.That(_owner.Money).IsEqualTo(50L);
        await Assert.That(_bag.Items).IsEmpty();
        await Assert.That(item.Count).IsEqualTo(0);
        await Assert.That(_allItems[1]).IsSameReferenceAs(item);
        await Assert.That(_deleted).IsEmpty();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task PackExchange_InstallsRewardAndWalletBeforeReentrantConsumption()
    {
        var pack = AddItem(1, 100, 1);
        Template(200);
        var observations = new List<(long Money, bool PackGone, bool RewardPresent, bool Repeated)>();
        _owner.Events.OnItemGather += (_, _) =>
        {
            using var repeated = new InventoryMutation(ItemTaskType.Invalid);
            observations.Add((_owner.Money, !_bag.Items.Contains(pack),
                _bag.Items.Any(item => item.TemplateId == 200), repeated.TryConsume(_bag, pack, 1)));
        };
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryConsume(_bag, pack, 1) && mutation.TryGrant(_bag, 200, 1) &&
                mutation.TryChangeMoney(_owner, 25) && mutation.Complete();
        }
        await Assert.That(result).IsTrue();
        await Assert.That(observations.Count).IsEqualTo(2);
        await Assert.That(observations.All(value => value == (125L, true, true, false))).IsTrue();
        await Assert.That(_deleted).IsEquivalentTo([1UL]);
    }

    [Test]
    public async Task LaterNewStackFailure_RestoresConsumedPackEarlierGrantAndBothWallets()
    {
        var pack = AddItem(1, 100, 1);
        Template(200).MaxCount = 2;
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;
        bool granted;
        bool completed;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryConsume(_bag, pack, 1);
            mutation.TryChangeMoney(_owner, -50);
            mutation.TryChangeMoney(_owner, 50, SlotType.Bank);
            granted = mutation.TryGrant(_bag, 200, 3);
            completed = mutation.Complete();
        }
        await Assert.That(granted).IsFalse();
        await Assert.That(completed).IsFalse();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(pack);
        await Assert.That(pack.Count).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_owner.Money2).IsEqualTo(0L);
        await Assert.That(_allItems.Keys).IsEquivalentTo([1UL]);
        await Assert.That(_deleted).IsEquivalentTo([1_000UL]);
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task CapacityFailureAfterStackMerge_RestoresCountsAndSlotOrder()
    {
        var first = AddItem(1, 100, 99);
        var second = AddItem(2, 200, 1);
        _bag.ContainerSize = 2;
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryGrant(_bag, 100, 2);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(first.Count).IsEqualTo(99);
        await Assert.That(_bag.Items.SequenceEqual([first, second])).IsTrue();
        await Assert.That(_bag.FreeSlotCount).IsEqualTo(0);
        await Assert.That(_allItems.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MailGrant_KeepsExistingAttachmentSeparateAndDoesNotCreditInventory()
    {
        var mail = _owner.Inventory.MailAttachments;
        var existing = AddItem(1, 100, 2);
        _bag.Items.Remove(existing);
        existing._holdingContainer = mail;
        existing.SlotType = SlotType.Mail;
        mail.Items.Add(existing);
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;
        IReadOnlyList<Item> granted;
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryGrant(mail, 100, 3, out granted) && mutation.Complete();
        }
        await Assert.That(result).IsTrue();
        await Assert.That(granted.Single().Id).IsEqualTo(1_000UL);
        await Assert.That(existing.Count).IsEqualTo(2);
        await Assert.That(mail.Items.Count).IsEqualTo(2);
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task MoveToBuyback_SucceedsWithoutTaskDestinationAndRetainsItemDetails()
    {
        var item = AddItem(1, 100, 2);
        item.Detail = [1, 2, 3];
        var buyback = new ItemContainer(_owner.Id, SlotType.None, false, _owner) { Owner = _owner };
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryMove(item, buyback) && mutation.TryChangeMoney(_owner, 10) && mutation.Complete();
        }
        await Assert.That(result).IsTrue();
        await Assert.That(buyback.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_bag.Items).IsEmpty();
        await Assert.That(item.Detail).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(_owner.Money).IsEqualTo(110L);
        await Assert.That(_allItems[1]).IsSameReferenceAs(item);
    }

    [Test]
    public async Task DuplicateReference_RejectsWithoutPayingOrRemovingEitherEntry()
    {
        var item = AddItem(1, 100, 2);
        _bag.Items.Add(item);
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryConsume(_bag, item, 2) && mutation.TryChangeMoney(_owner, 10);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(_bag.Items.Count).IsEqualTo(2);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    public async Task FailedAdoption_ReleasesCreatedItemAndPreservesExistingBag()
    {
        var item = AddItem(1, 100, 1);
        _bag.ContainerSize = 1;
        var created = new ItemMock(2, Template(200), 1) { Detail = [7, 8] };
        _allItems.Add(2, created);
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryAddCreated(created, _bag);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_allItems.Keys).IsEquivalentTo([1UL]);
        await Assert.That(_deleted).IsEquivalentTo([2UL]);
    }

    [Test]
    public async Task RepeatedGrant_EmitsOriginalCreateCountFollowedByOnlyTheAddedDelta()
    {
        Template(100);
        byte[] firstBytes;
        byte[] expected;
        byte[] updateBytes;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryGrant(_bag, 100, 2, out var granted);
            expected = new ItemAdd(granted.Single()).Write(new PacketStream()).GetBytes();
            mutation.TryGrant(_bag, 100, 3);
            var tasks = PreparedTasks(mutation);
            firstBytes = tasks[0].Write(new PacketStream()).GetBytes();
            updateBytes = tasks[1].Write(new PacketStream()).GetBytes();
            mutation.Complete();
        }
        await Assert.That(firstBytes).IsEquivalentTo(expected);
        var update = new PacketStream(updateBytes);
        await Assert.That(update.ReadByte()).IsEqualTo((byte)ItemAction.AddStack);
        update.ReadByte();
        update.ReadByte();
        update.ReadUInt64();
        await Assert.That(update.ReadInt32()).IsEqualTo(3);
        await Assert.That(_bag.Items.Single().Count).IsEqualTo(5);
    }

    [Test]
    public async Task PartialConsumeThenMove_EmitsCountChangeAtItsOriginalLocation()
    {
        var item = AddItem(1, 100, 3);
        byte[] updateBytes;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryConsume(_bag, item, 1);
            mutation.TryMove(item, _owner.Inventory.Warehouse, 5);
            updateBytes = PreparedTasks(mutation)[0].Write(new PacketStream()).GetBytes();
            mutation.Complete();
        }
        var update = new PacketStream(updateBytes);
        update.ReadByte();
        await Assert.That(update.ReadByte()).IsEqualTo((byte)SlotType.Inventory);
        await Assert.That(update.ReadByte()).IsEqualTo((byte)0);
        await Assert.That(item.SlotType).IsEqualTo(SlotType.Bank);
        await Assert.That(item.Slot).IsEqualTo(5);
    }

    [Test]
    public async Task ItemTimer_WaitsForPreparedExchangeToDisposeBeforeChangingItsLifetime()
    {
        var item = AddItem(1, 100, 1);
        item.ExpirationOnlineMinutesLeft = 10;
        var timer = typeof(ItemManager).GetMethod("UpdateItemContainerTimers", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var started = new ManualResetEventSlim();
        Task timerWork;
        bool completedEarly;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryConsume(_bag, item, 1);
            timerWork = Task.Run(() =>
            {
                started.Set();
                timer.Invoke(null, [TimeSpan.FromMinutes(1), _bag, _owner]);
            });
            started.Wait(TimeSpan.FromSeconds(10));
            completedEarly = timerWork.Wait(TimeSpan.FromMilliseconds(100));
        }
        await timerWork.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(completedEarly).IsFalse();
        await Assert.That(item.ExpirationOnlineMinutesLeft).IsEqualTo(9.0);
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
    }

    private List<ItemTask> PreparedTasks(InventoryMutation mutation)
    {
        var tasks = (Dictionary<ICharacter, List<ItemTask>>)typeof(InventoryMutation)
            .GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mutation)!;
        return tasks[_owner];
    }

    [Test]
    public async Task SecondMoveOfSameItem_RejectsAndRestoresTheOriginalContainer()
    {
        var item = AddItem(1, 100, 1);
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryMove(item, _owner.Inventory.Warehouse);
            result = mutation.TryMove(item, _bag);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item._holdingContainer).IsSameReferenceAs(_bag);
        await Assert.That(_owner.Inventory.Warehouse.Items).IsEmpty();
    }

    [Test]
    public async Task PreparedItemSerialization_DoesNotOverwriteRawItemDetails()
    {
        var item = new Item(0, 1, Template(100), 1)
        {
            OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = 0, _holdingContainer = _bag,
            DetailType = ItemDetailType.Glider, Detail = [1, 2, 3, 4]
        };
        _bag.Items.Add(item);
        var detail = item.Detail;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryMove(item, _owner.Inventory.Warehouse);
            mutation.TryChangeMoney(_owner, -101);
        }
        await Assert.That(item.Detail).IsSameReferenceAs(detail);
        await Assert.That(item.Detail).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }

    [Test]
    public async Task FullBags_ExchangeOriginalItemsAndRemoveBothOutgoingSlotsBeforeAdding()
    {
        var other = AddOwner(8);
        var left = AddItem(1, 100, 3);
        var right = AddItem(2, 200, 2, other);
        _bag.ContainerSize = other.Inventory.Bag.ContainerSize = 1;
        ItemAction[] leftActions;
        ItemAction[] rightActions;
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Trade);
            result = mutation.TryExchange([(left, 3, other.Inventory.Bag), (right, 2, _bag)]);
            leftActions = mutation.GetTasks(_owner).Select(TaskAction).ToArray();
            rightActions = mutation.GetTasks(other).Select(TaskAction).ToArray();
            result &= mutation.Complete(false);
        }
        await Assert.That(result).IsTrue();
        await Assert.That(leftActions).IsEquivalentTo(new[] { ItemAction.Seize, ItemAction.Create });
        await Assert.That(rightActions).IsEquivalentTo(new[] { ItemAction.Seize, ItemAction.Create });
        await Assert.That(leftActions[0]).IsEqualTo(ItemAction.Seize);
        await Assert.That(rightActions[0]).IsEqualTo(ItemAction.Seize);
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(right);
        await Assert.That(other.Inventory.Bag.Items.Single()).IsSameReferenceAs(left);
        await Assert.That(right.OwnerId).IsEqualTo((ulong)_owner.Id);
        await Assert.That(left.OwnerId).IsEqualTo((ulong)other.Id);
        await Assert.That(left.Slot).IsEqualTo(0);
        await Assert.That(right.Slot).IsEqualTo(0);
        await Assert.That(_allItems.Count).IsEqualTo(2);
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    public async Task PartialExchange_PreservesOriginalAndCreatesOnlyOfferedQuantityWithIndependentDetails()
    {
        var other = AddOwner(8);
        var item = AddItem(1, 100, 7);
        item.Detail = [7, 8];
        item.ExpirationTime = DateTime.UtcNow.AddDays(1);
        item.ExpirationOnlineMinutesLeft = 12;
        Item split;
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryExchange([(item, 3, other.Inventory.Bag)]) && mutation.Complete(false);
            split = other.Inventory.Bag.Items.Single();
        }
        await Assert.That(result).IsTrue();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(4);
        await Assert.That(split.Count).IsEqualTo(3);
        await Assert.That(split.Id).IsEqualTo(1_000UL);
        await Assert.That(split.OwnerId).IsEqualTo((ulong)other.Id);
        await Assert.That(split.GetType()).IsEqualTo(item.GetType());
        await Assert.That(split.Detail).IsEquivalentTo(item.Detail);
        split.Detail[0] = 1;
        await Assert.That(item.Detail[0]).IsEqualTo((byte)7);
        await Assert.That(split.ExpirationTime).IsEqualTo(item.ExpirationTime);
        await Assert.That(split.ExpirationOnlineMinutesLeft).IsEqualTo(12.0);
        await Assert.That(_allItems[1_000]).IsSameReferenceAs(split);
    }

    [Test]
    public async Task LaterExchangeCapacityFailure_RestoresSplitFullItemSlotsWalletsAndDirtyFlags()
    {
        var other = AddOwner(8);
        var partial = AddItem(1, 100, 7);
        var full = AddItem(2, 200, 1);
        var existing = AddItem(3, 300, 1, other);
        other.Inventory.Bag.ContainerSize = 2;
        partial.IsDirty = full.IsDirty = _bag.IsDirty = other.Inventory.Bag.IsDirty = false;
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;
        other.Events.OnItemGather += (_, _) => events++;
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            mutation.TryChangeMoney(_owner, -30);
            mutation.TryChangeMoney(other, 30);
            result = mutation.TryExchange([(partial, 3, other.Inventory.Bag), (full, 1, other.Inventory.Bag)]);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(_bag.Items.SequenceEqual([partial, full])).IsTrue();
        await Assert.That(other.Inventory.Bag.Items.Single()).IsSameReferenceAs(existing);
        await Assert.That(partial.Count).IsEqualTo(7);
        await Assert.That(full._holdingContainer).IsSameReferenceAs(_bag);
        await Assert.That(full.Slot).IsEqualTo(1);
        await Assert.That(partial.IsDirty).IsFalse();
        await Assert.That(full.IsDirty).IsFalse();
        await Assert.That(_bag.IsDirty).IsFalse();
        await Assert.That(other.Inventory.Bag.IsDirty).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(other.Money).IsEqualTo(100L);
        await Assert.That(_allItems.Keys).IsEquivalentTo(new ulong[] { 1, 2, 3 });
        await Assert.That(_deleted).IsEquivalentTo(new ulong[] { 1_000 });
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task SplitIdCollision_LeavesExistingIdRegisteredAndRestoresSource()
    {
        var other = AddOwner(8);
        var source = AddItem(1, 100, 4);
        var collision = AddItem(1_000, 200, 1);
        bool result;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            result = mutation.TryExchange([(source, 2, other.Inventory.Bag)]);
        }
        await Assert.That(result).IsFalse();
        await Assert.That(source.Count).IsEqualTo(4);
        await Assert.That(_allItems[1_000]).IsSameReferenceAs(collision);
        await Assert.That(other.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    public async Task SplitCopies_PreserveEquipmentGemsAndFishFieldsAndSerializeOfferedCount()
    {
        var equipment = new EquipItem(1, new EquipItemTemplate { Id = 100, MaxCount = 10 }, 5)
        {
            GemIds = [1, 2, 3, 4, 5, 6, 7], Detail = [9], Durability = 31,
            RuneId = 52, TemperPhysical = 80, TemperMagical = 81
        };
        var copy = (EquipItem)equipment.CopyForSplit(2, 2);
        copy.GemIds[0] = 99;
        copy.Detail[0] = 0;
        await Assert.That(equipment.GemIds[0]).IsEqualTo(1U);
        await Assert.That(equipment.Detail[0]).IsEqualTo((byte)9);
        await Assert.That(copy.Durability).IsEqualTo((byte)31);
        await Assert.That(copy.RuneId).IsEqualTo(52U);
        await Assert.That(copy.TemperPhysical).IsEqualTo((ushort)80);
        await Assert.That(copy.TemperMagical).IsEqualTo((ushort)81);
        var fish = new BigFish(3, Template(200), 5) { Weight = 77.5f, Length = 25.25f };
        var fishCopy = (BigFish)fish.CopyForSplit(4, 2);
        await Assert.That(fishCopy.Weight).IsEqualTo(77.5f);
        await Assert.That(fishCopy.Length).IsEqualTo(25.25f);
        var wire = new PacketStream(fish.Write(new PacketStream(), 2).GetBytes());
        wire.ReadUInt32();
        wire.ReadUInt64();
        wire.ReadByte();
        wire.ReadByte();
        await Assert.That(wire.ReadInt32()).IsEqualTo(2);
        await Assert.That(fish.Count).IsEqualTo(5);
        await Assert.That(fish.Weight).IsEqualTo(77.5f);
    }

    [Test]
    public async Task ReservedQuantity_OrdinaryConsumptionOnlyUsesUnreservedUnitsThenReleaseRestoresAccess()
    {
        var item = AddItem(1, 100, 10);
        using var reservation = new TradeReservation();
        await Assert.That(reservation.TryReserve(item, 7)).IsTrue();
        await Assert.That(_bag.ConsumeItem(ItemTaskType.Invalid, 100, 9, item)).IsEqualTo(3);
        await Assert.That(item.Count).IsEqualTo(7);
        await Assert.That(_bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 1 })).IsFalse();
        await Assert.That(_bag.ConsumeItem(ItemTaskType.Invalid, 100, 1, null)).IsEqualTo(0);
        reservation.Release(item);
        await Assert.That(_bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 7 })).IsTrue();
        await Assert.That(_bag.Items).IsEmpty();
    }

    [Test]
    public async Task FullyReservedForeignPreferredItem_DoesNotConsumeAnUnrelatedLocalStack()
    {
        var local = AddItem(1, 100, 5);
        var foreign = AddItem(2, 100, 5, AddOwner(8));
        using var reservation = new TradeReservation();
        reservation.TryReserve(foreign, 5);
        await Assert.That(_bag.ConsumeItem(ItemTaskType.Invalid, 100, 2, foreign)).IsEqualTo(0);
        await Assert.That(local.Count).IsEqualTo(5);
        await Assert.That(foreign.Count).IsEqualTo(5);
    }

    [Test]
    public async Task ReservedQuantity_BlocksContainerRemovalMovementSplitAndStagedExchange()
    {
        var item = AddItem(1, 100, 10);
        var other = AddOwner(8);
        using var reservation = new TradeReservation();
        reservation.TryReserve(item, 7);
        await Assert.That(_bag.RemoveItem(ItemTaskType.Invalid, item, true)).IsFalse();
        await Assert.That(_owner.Inventory.Warehouse.AddOrMoveExistingItem(ItemTaskType.Invalid, item)).IsFalse();
        await Assert.That(_owner.Inventory.SplitOrMoveItemEx(ItemTaskType.Invalid, _bag, _bag,
            item.Id, SlotType.Inventory, 0, 0, SlotType.Inventory, 1, 1)).IsFalse();
        bool moved;
        bool exchanged;
        bool consumed;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using (var mutation = new InventoryMutation(ItemTaskType.Invalid))
                moved = mutation.TryMove(item, other.Inventory.Bag);
            using (var mutation = new InventoryMutation(ItemTaskType.Invalid))
                exchanged = mutation.TryExchange([(item, 1, other.Inventory.Bag)]);
            using (var mutation = new InventoryMutation(ItemTaskType.Invalid))
                consumed = mutation.TryConsume(_bag, item, 4);
        }
        await Assert.That(moved).IsFalse();
        await Assert.That(exchanged).IsFalse();
        await Assert.That(consumed).IsFalse();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(10);
        await Assert.That(_allItems[1]).IsSameReferenceAs(item);
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    public async Task ReservedMoney_BlocksSpendingAndBankDepositButAllowsUnreservedFundsAndBankWithdrawal()
    {
        _owner.Money2 = 50;
        using var reservation = new TradeReservation();
        await Assert.That(reservation.TryReserve(_owner, 70)).IsTrue();
        await Assert.That(_owner.SubtractMoney(SlotType.Inventory, 31)).IsFalse();
        await Assert.That(_owner.ChangeMoney(SlotType.Inventory, SlotType.Bank, 31)).IsFalse();
        await Assert.That(_owner.ChangeMoney(SlotType.Inventory, -31)).IsFalse();
        bool staged;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            staged = mutation.TryChangeMoney(_owner, -31);
        }
        await Assert.That(staged).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_owner.Money2).IsEqualTo(50L);
        await Assert.That(_owner.SubtractMoney(SlotType.Inventory, 30)).IsTrue();
        await Assert.That(_owner.ChangeMoney(SlotType.Bank, SlotType.Inventory, 10)).IsTrue();
        await Assert.That(_owner.Money).IsEqualTo(80L);
        await Assert.That(_owner.Money2).IsEqualTo(40L);
        reservation.Dispose();
        await Assert.That(_owner.SubtractMoney(SlotType.Inventory, 80)).IsTrue();
    }

    [Test]
    public async Task Reservations_AreExclusiveAndReplacementOrDisposalDoesNotAffectAnotherTrade()
    {
        var item = AddItem(1, 100, 4);
        var other = AddOwner(8);
        using var first = new TradeReservation();
        using var second = new TradeReservation();
        await Assert.That(first.TryReserve(item, 2)).IsTrue();
        await Assert.That(second.TryReserve(item, 1)).IsFalse();
        await Assert.That(first.TryReserve(_owner, 70)).IsTrue();
        await Assert.That(second.TryReserve(_owner, 10)).IsFalse();
        await Assert.That(first.TryReserve(_owner, 101)).IsFalse();
        await Assert.That(TradeReservation.GetReservedMoney(_owner)).IsEqualTo(70);
        await Assert.That(first.TryReserve(_owner, 40)).IsTrue();
        await Assert.That(second.TryReserve(other, 50)).IsTrue();
        second.Release(item);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(2);
        await Assert.That(TradeReservation.HasReservations(_owner)).IsTrue();
        first.Dispose();
        first.Dispose();
        await Assert.That(TradeReservation.HasReservations(_owner)).IsFalse();
        await Assert.That(TradeReservation.GetReservedMoney(other)).IsEqualTo(50);
        await Assert.That(second.TryReserve(item, 3)).IsTrue();
    }

    [Test]
    public async Task OfferedItemExpiry_InvalidatesWholeTradeBeforeRemovingExpiredItem()
    {
        var expiring = AddItem(1, 100, 1);
        var retained = AddItem(2, 200, 1);
        expiring.ExpirationOnlineMinutesLeft = 1;
        var invalidations = 0;
        var clearedBeforeNotification = false;
        using var reservation = new TradeReservation(() =>
        {
            invalidations++;
            clearedBeforeNotification = TradeReservation.GetReservedCount(retained) == 0 &&
                TradeReservation.GetReservedMoney(_owner) == 0 && _bag.Items.Contains(expiring);
        });
        reservation.TryReserve(expiring, 1);
        reservation.TryReserve(retained, 1);
        reservation.TryReserve(_owner, 80);
        var timer = typeof(ItemManager).GetMethod("UpdateItemContainerTimers", BindingFlags.Static | BindingFlags.NonPublic)!;
        timer.Invoke(null, [TimeSpan.FromMinutes(2), _bag, _owner]);
        await Assert.That(invalidations).IsEqualTo(1);
        await Assert.That(clearedBeforeNotification).IsTrue();
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(retained);
        await Assert.That(_deleted).IsEquivalentTo(new ulong[] { 1 });
        await Assert.That(TradeReservation.HasReservations(_owner)).IsFalse();
    }

    [Test]
    public async Task OfferedItemExpiry_StillRemovesExpiredItemWhenCancellationNotificationThrows()
    {
        var item = AddItem(1, 100, 1);
        item.ExpirationOnlineMinutesLeft = 1;
        using var reservation = new TradeReservation(() => throw new IOException("Notification failed"));
        reservation.TryReserve(item, 1);
        reservation.TryReserve(_owner, 80);
        var timer = typeof(ItemManager).GetMethod("UpdateItemContainerTimers", BindingFlags.Static | BindingFlags.NonPublic)!;
        timer.Invoke(null, [TimeSpan.FromMinutes(2), _bag, _owner]);
        await Assert.That(_bag.Items).IsEmpty();
        await Assert.That(_deleted).IsEquivalentTo(new ulong[] { 1 });
        await Assert.That(TradeReservation.HasReservations(_owner)).IsFalse();
    }

    [Test]
    public async Task DestroyPacket_CannotDestroyReservedUnitsButCanDestroyTheUnreservedRemainder()
    {
        var item = AddItem(1, 100, 5);
        using var reservation = new TradeReservation();
        reservation.TryReserve(item, 3);
        var packet = new CSDestroyItemPacket { Connection = new GameConnection(null) { ActiveChar = _owner } };
        packet.Read(new PacketStream().Write(item.Id).Write((byte)SlotType.Inventory).Write((byte)0).Write(4));
        await Assert.That(item.Count).IsEqualTo(5);
        packet.Read(new PacketStream().Write(item.Id).Write((byte)SlotType.Inventory).Write((byte)0).Write(2));
        await Assert.That(item.Count).IsEqualTo(3);
        await Assert.That(_bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Expansion_DoesNotDebitOrIncreaseCapacityWhenItsMaterialsOrGoldAreReserved(bool bank)
    {
        var manager = new CharacterManager(null, null, null, null, null, null, null, null, null, null, null);
        var managerField = typeof(Singleton<CharacterManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = managerField.GetValue(null);
        managerField.SetValue(null, manager);
        try
        {
            SetField(manager, "_expands", new Dictionary<int, List<Expand>>
            {
                [0] = [new Expand { IsBank = bank, Price = 25, ItemId = 100, ItemCount = 2 }]
            });
            _owner.NumInventorySlots = 50;
            _owner.NumBankSlots = 50;
            _bag.ContainerSize = _owner.Inventory.Warehouse.ContainerSize = 50;
            var item = AddItem(1, 100, 4);
            using var reservation = new TradeReservation();
            reservation.TryReserve(item, 3);
            var type = bank ? SlotType.Bank : SlotType.Inventory;
            _owner.Inventory.ExpandSlot(type);
            await Assert.That(_owner.Money).IsEqualTo(100L);
            await Assert.That(item.Count).IsEqualTo(4);
            await Assert.That(_bag.ContainerSize).IsEqualTo(50);
            await Assert.That(_owner.Inventory.Warehouse.ContainerSize).IsEqualTo(50);
            reservation.Release(item);
            reservation.TryReserve(_owner, 80);
            _owner.Inventory.ExpandSlot(type);
            await Assert.That(_owner.Money).IsEqualTo(100L);
            await Assert.That(item.Count).IsEqualTo(4);
            reservation.Dispose();
            _owner.Inventory.ExpandSlot(type);
            await Assert.That(_owner.Money).IsEqualTo(75L);
            await Assert.That(item.Count).IsEqualTo(2);
            await Assert.That(bank ? _owner.Inventory.Warehouse.ContainerSize : _bag.ContainerSize).IsEqualTo(60);
        }
        finally
        {
            managerField.SetValue(null, previous);
        }
    }

    private static ItemAction TaskAction(ItemTask task) =>
        (ItemAction)new PacketStream(task.Write(new PacketStream()).GetBytes()).ReadByte();

    private CharacterMock AddOwner(uint id)
    {
        var owner = new CharacterMock { Id = id, Money = 100, NumInventorySlots = 10, NumBankSlots = 10 };
        var containers = (Dictionary<ulong, ItemContainer>)typeof(ItemManager)
            .GetField("_allPersistentContainers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ItemManager.Instance)!;
        var nextId = containers.Keys.Max() + 1;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(owner.Id, slotType, false, owner)
                { ContainerId = nextId++, Owner = owner };
            containers.Add(container.ContainerId, container);
        }
        owner.Inventory = new Inventory(owner);
        return owner;
    }

    private ItemMock AddItem(uint id, uint templateId, int count, CharacterMock owner)
    {
        var bag = owner.Inventory.Bag;
        var item = new ItemMock(id, Template(templateId), count)
        {
            OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = bag.Items.Count,
            _holdingContainer = bag
        };
        bag.Items.Add(item);
        bag.UpdateFreeSlotCount();
        _allItems.Add(id, item);
        return item;
    }

    private ItemTemplate Template(uint id)
    {
        if (!_templates.TryGetValue(id, out var template))
            _templates.Add(id, template = new ItemTemplate { Id = id, MaxCount = 100, BindType = ItemBindType.Normal });
        return template;
    }

    private ItemMock AddItem(uint id, uint templateId, int count)
    {
        var item = new ItemMock(id, Template(templateId), count)
        {
            OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = _bag.Items.Count,
            _holdingContainer = _bag
        };
        _bag.Items.Add(item);
        _bag.UpdateFreeSlotCount();
        _allItems.Add(id, item);
        return item;
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
