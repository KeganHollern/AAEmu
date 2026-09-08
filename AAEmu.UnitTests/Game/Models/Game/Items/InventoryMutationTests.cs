using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
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
