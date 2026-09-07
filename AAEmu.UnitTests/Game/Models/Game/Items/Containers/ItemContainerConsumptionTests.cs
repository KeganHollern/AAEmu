using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items.Containers;

[NotInParallel]
public sealed class ItemContainerConsumptionTests
{
    private static readonly FieldInfo s_itemManagerInstance = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_questManagerInstance = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private ItemManager _previousItems;
    private QuestManager _previousQuests;
    private ItemManager _items;
    private Dictionary<ulong, Item> _allItems;
    private List<ulong> _removedItems;
    private Dictionary<uint, ItemTemplate> _templates;
    private CharacterMock _owner;
    private ItemContainer _bag;

    [Before(Test)]
    public void SetUp()
    {
        _previousItems = (ItemManager)s_itemManagerInstance.GetValue(null);
        _previousQuests = (QuestManager)s_questManagerInstance.GetValue(null);
        var ids = Mock.Of<IItemIdManager>();
        ids.GetNextId().Returns(1_000U);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        s_itemManagerInstance.SetValue(null, _items);
        s_questManagerInstance.SetValue(null,
            new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        _removedItems = [];
        _templates = [];
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_removedItems", _removedItems);
        SetField(_items, "_templates", _templates);
        _owner = new CharacterMock { Id = 7, NumInventorySlots = 10, NumBankSlots = 10 };
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
        SetField(_items, "_allPersistentContainers", containers);
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
    [Arguments(0)]
    [Arguments(1)]
    public async Task MissingOrInsufficientLaterTemplate_ChangesNothingAndEmitsNoEvents(int secondCount)
    {
        var first = AddItem(1, 100, 4);
        var second = secondCount > 0 ? AddItem(2, 200, secondCount) : null;
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;

        var result = _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 3, [200] = 2 });

        await Assert.That(result).IsFalse();
        await Assert.That(first.Count).IsEqualTo(4);
        await Assert.That(second?.Count ?? 0).IsEqualTo(secondCount);
        await Assert.That(_removedItems).IsEmpty();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task BatchAcrossStacksAndTemplates_AppliesEverythingBeforeFirstCallback()
    {
        var first = AddItem(1, 100, 2);
        var remainder = AddItem(2, 100, 5);
        var second = AddItem(3, 200, 3);
        var observations = new List<(uint TemplateId, int Delta, int Remaining, bool FirstRemoved, bool SecondRemoved)>();
        _owner.Events.OnItemGather += (_, args) => observations.Add((args.ItemId, args.Count,
            remainder.Count, !_bag.Items.Contains(first), !_bag.Items.Contains(second)));

        var result = _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 4, [200] = 3 });

        await Assert.That(result).IsTrue();
        await Assert.That(remainder.Count).IsEqualTo(3);
        await Assert.That(_bag.Items).HasSingleItem();
        await Assert.That(_bag.FreeSlotCount).IsEqualTo(9);
        await Assert.That(_removedItems).IsEquivalentTo([1UL, 3UL]);
        await Assert.That(observations).IsEquivalentTo([
            (100U, -2, 3, true, true), (100U, -2, 3, true, true), (200U, -3, 3, true, true)]);
    }

    [Test]
    public async Task ReentrantConsumption_SeesTheCompletedBatch()
    {
        AddItem(1, 100, 2);
        AddItem(2, 200, 2);
        var nestedResults = new List<bool>();
        _owner.Events.OnItemGather += (_, _) => nestedResults.Add(
            _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [200] = 1 }));

        var result = _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 2, [200] = 2 });

        await Assert.That(result).IsTrue();
        await Assert.That(nestedResults).IsEquivalentTo([false, false]);
        await Assert.That(_bag.Items).IsEmpty();
    }

    [Test]
    public async Task NonDestroyableLaterStack_LeavesTheWholeBatchIntact()
    {
        var first = AddItem(1, 100, 2);
        var protectedItem = new ProtectedItem(2, Template(200)) { Count = 1 };
        PutItem(protectedItem);

        var result = _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 2, [200] = 1 });

        await Assert.That(result).IsFalse();
        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(protectedItem.Count).IsEqualTo(1);
        await Assert.That(_bag.Items.Count).IsEqualTo(2);
        await Assert.That(_removedItems).IsEmpty();
    }

    [Test]
    public async Task CompetingBatches_OnlyOneCanConsumeTheSameItems()
    {
        AddItem(1, 100, 3);
        AddItem(2, 200, 2);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 3, [200] = 2 });
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(results.Count(success => success)).IsEqualTo(1);
        await Assert.That(_removedItems).IsEquivalentTo([1UL, 2UL]);
        await Assert.That(_bag.Items).IsEmpty();
    }

    [Test]
    public async Task OrdinaryConsumeWaitsUntilTheBatchFinishes()
    {
        using var validating = new ManualResetEventSlim();
        using var allowCommit = new ManualResetEventSlim();
        using var competitorStarted = new ManualResetEventSlim();
        var first = new ProtectedItem(1, Template(100))
        {
            Count = 2,
            DestroyCheck = () => { validating.Set(); return allowCommit.Wait(TimeSpan.FromSeconds(10)); }
        };
        PutItem(first);
        AddItem(2, 200, 2);
        var batch = Task.Run(() => _bag.TryConsumeItems(ItemTaskType.Invalid,
            new Dictionary<uint, int> { [100] = 2, [200] = 2 }));
        var ready = validating.Wait(TimeSpan.FromSeconds(10));
        var competitor = Task.Run(() =>
        {
            competitorStarted.Set();
            return _bag.ConsumeItem(ItemTaskType.Invalid, 200, 1, null);
        });
        competitorStarted.Wait(TimeSpan.FromSeconds(10));
        var completedEarly = competitor.Wait(TimeSpan.FromMilliseconds(100));
        allowCommit.Set();
        var batchResult = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        var consumed = await competitor.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(ready).IsTrue();
        await Assert.That(completedEarly).IsFalse();
        await Assert.That(batchResult).IsTrue();
        await Assert.That(consumed).IsEqualTo(0);
        await Assert.That(_removedItems).IsEquivalentTo([1UL, 2UL]);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task InvalidRequirement_ChangesNoItems(int requiredCount)
    {
        var item = AddItem(1, 100, 3);
        var result = _bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = requiredCount });
        await Assert.That(result).IsFalse();
        await Assert.That(item.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcquisitionRecordsActualGrantBeforeInventoryCallback(bool existingStack)
    {
        Template(100);
        if (existingStack)
            AddItem(1, 100, 2);
        var credited = 0;
        var observedCredit = 0;
        _owner.Events.OnItemGather += (_, args) => { if (args.Count > 0) observedCredit = credited; };

        var result = _bag.AcquireDefaultItemEx(ItemTaskType.Invalid, 100, 3, 0, out var created,
            out var updated, 0, onGranted: count => credited += count);

        await Assert.That(result).IsTrue();
        await Assert.That(credited).IsEqualTo(3);
        await Assert.That(observedCredit).IsEqualTo(3);
        await Assert.That(created.Count).IsEqualTo(existingStack ? 0 : 1);
        await Assert.That(updated.Count).IsEqualTo(existingStack ? 1 : 0);
    }

    [Test]
    public async Task MailAttachmentCreation_DoesNotCreditAcquisitionBeforeClaim()
    {
        Template(100);
        var events = 0;
        _owner.Events.OnItemGather += (_, _) => events++;
        var granted = 0;

        var result = _owner.Inventory.MailAttachments.AcquireDefaultItemEx(ItemTaskType.Invalid, 100, 3, 0,
            out var created, out _, 0, onGranted: count => granted += count);

        await Assert.That(result).IsTrue();
        await Assert.That(created).HasSingleItem();
        await Assert.That(granted).IsEqualTo(3);
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task LaterNewItemFailure_PreservesEarlierGrantAndReturnsFalse()
    {
        Template(100).MaxCount = 2;
        var granted = 0;
        // The fixture ID allocator repeats 1000: the second generated stack cannot be registered.
        var result = _bag.AcquireDefaultItemEx(ItemTaskType.Invalid, 100, 3, 0, out var created,
            out var updated, 0, onGranted: count => granted += count);

        await Assert.That(result).IsFalse();
        await Assert.That(created).HasSingleItem();
        await Assert.That(created[0].Count).IsEqualTo(2);
        await Assert.That(updated).IsEmpty();
        await Assert.That(granted).IsEqualTo(2);
        await Assert.That(_bag.Items).HasSingleItem();
    }

    [Test]
    public async Task NewStackRejectedAfterExistingGrant_ReturnsPartialProgressAndReleasesUnusedId()
    {
        var existing = AddItem(1, 100, 99);
        var granted = 0;

        // A synchronous acquisition callback can use the last free slot before the next stack.
        var result = _bag.AcquireDefaultItemEx(ItemTaskType.Invalid, 100, 2, 0, out var created,
            out var updated, 0, onGranted: count =>
            {
                granted += count;
                _bag.ContainerSize = 1;
            });

        await Assert.That(result).IsFalse();
        await Assert.That(existing.Count).IsEqualTo(100);
        await Assert.That(granted).IsEqualTo(1);
        await Assert.That(created).IsEmpty();
        await Assert.That(updated).HasSingleItem();
        await Assert.That(updated[0]).IsSameReferenceAs(existing);
        await Assert.That(_removedItems).IsEquivalentTo([1_000UL]);
        await Assert.That(_items.GetItemByItemId(1_000)).IsNull();
        await Assert.That(_bag.Items).HasSingleItem();
    }

    [Test]
    public async Task HouseTaxGate_PreventsConcurrentInventoryMutation()
    {
        var item = AddItem(1, 100, 3);
        var house = new House();
        using var started = new ManualResetEventSlim();
        Task<int> consume;
        bool completedEarly;
        int countDuringHouseOperation;
        lock (house.TaxPaymentSyncRoot)
        {
            consume = Task.Run(() =>
            {
                started.Set();
                return _bag.ConsumeItem(ItemTaskType.Invalid, 100, 1, null);
            });
            started.Wait(TimeSpan.FromSeconds(10));
            completedEarly = consume.Wait(TimeSpan.FromMilliseconds(100));
            countDuringHouseOperation = item.Count;
        }
        var consumed = await consume.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(completedEarly).IsFalse();
        await Assert.That(countDuringHouseOperation).IsEqualTo(3);
        await Assert.That(consumed).IsEqualTo(1);
        await Assert.That(item.Count).IsEqualTo(2);
    }

    private ItemTemplate Template(uint templateId)
    {
        if (!_templates.TryGetValue(templateId, out var template))
        {
            template = new ItemTemplate { Id = templateId, MaxCount = 100, BindType = ItemBindType.Normal };
            _templates.Add(templateId, template);
        }
        return template;
    }

    private ItemMock AddItem(uint id, uint templateId, int count)
    {
        var item = new ItemMock(id, Template(templateId), count);
        PutItem(item);
        return item;
    }

    private void PutItem(Item item)
    {
        item.OwnerId = _owner.Id;
        item.SlotType = SlotType.Inventory;
        item.Slot = _bag.Items.Count;
        item._holdingContainer = _bag;
        _bag.Items.Add(item);
        _bag.UpdateFreeSlotCount();
        _allItems.Add(item.Id, item);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ProtectedItem(uint id, ItemTemplate template) : ItemMock(id, template)
    {
        public Func<bool> DestroyCheck { get; init; }
        public override bool CanDestroy() => DestroyCheck?.Invoke() ?? false;
    }
}
