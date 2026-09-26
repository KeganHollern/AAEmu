using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

[NotInParallel]
public sealed class QuestItemGroupObjectiveTests
{
    private const uint ActId = 1_012;

    private static readonly FieldInfo s_itemManagerInstanceField = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_questManagerInstanceField = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private IServiceProvider _previousServiceProvider;
    private ItemManager _previousItemManager;
    private QuestManager _previousQuestManager;
    private ServiceProvider _testServiceProvider;
    private ItemManager _itemManager;
    private QuestManager _questManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        _previousItemManager = (ItemManager)s_itemManagerInstanceField.GetValue(null);
        _previousQuestManager = (QuestManager)s_questManagerInstanceField.GetValue(null);

        _itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);
        _questManager = new QuestManager(
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IZoneManager>().Object);

        var services = new ServiceCollection();
        services.AddSingleton(_itemManager);
        services.AddSingleton(_questManager);
        _testServiceProvider = services.BuildServiceProvider();

        s_itemManagerInstanceField.SetValue(null, null);
        s_questManagerInstanceField.SetValue(null, null);
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        s_itemManagerInstanceField.SetValue(null, _previousItemManager);
        s_questManagerInstanceField.SetValue(null, _previousQuestManager);
        _testServiceProvider?.Dispose();
    }

    [Test]
    [Arguments(5490u, 9u, 10, true)]
    [Arguments(6578u, 14u, 1, false)]
    [Arguments(6600u, 15u, 1, false)]
    [Arguments(6615u, 16u, 1, false)]
    public async Task LiveGatherQuest_CompletesFromGroupItemsAndHonorsCleanup(
        uint questId, uint groupId, int count, bool cleanup)
    {
        var (owner, first, second) = CreateInventory(groupId, 0, 0);
        var (quest, act, template) = CreateGatherQuest(owner, questId, groupId, count, cleanup);
        await Assert.That(template.CountsAsAnObjective).IsTrue();
        await Assert.That(act.RunAct()).IsFalse();

        first.Count = count / 2;
        owner.Inventory.OnAcquiredItem(first, first.Count, true);
        await Assert.That(act.RunAct()).IsFalse();
        second.Count = count - first.Count;
        owner.Inventory.OnAcquiredItem(second, second.Count, true);
        await Assert.That(act.RunAct()).IsTrue();
        await Assert.That(quest.Objectives[0]).IsEqualTo(count);

        quest.Complete();
        await Assert.That(first.Count + second.Count).IsEqualTo(cleanup ? 0 : count);
    }

    [Test]
    public async Task Gather_RecountsPreexistingItemsRemovalsAndOverflowWithoutDuplicateGroupMembers()
    {
        var (owner, first, second) = CreateInventory(9, 7, 5);
        var (_, act, _) = CreateGatherQuest(owner, 5490, 9, 10, false);
        await Assert.That(act.RunAct()).IsTrue();

        owner.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, first.TemplateId, 4, first);
        await Assert.That(act.GetObjective(act.QuestComponent.Parent.Parent)).IsEqualTo(8);
        await Assert.That(act.RunAct()).IsFalse();
        first.Count += 2;
        owner.Inventory.OnAcquiredItem(first, 2, true);
        await Assert.That(act.RunAct()).IsTrue();
        await Assert.That(second.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Gather_WrongGroupOrItemAndAfterFinalize_DoNotChangeProgress()
    {
        var (owner, first, _) = CreateInventory(9, 0, 0);
        var (quest, _, _) = CreateGatherQuest(owner, 5490, 9, 10, false);
        first.Count = 4;
        owner.Events.OnItemGroupGather(owner, new OnItemGroupGatherArgs
            { ItemGroupId = 14, ItemId = first.TemplateId, Count = 4 });
        owner.Events.OnItemGroupGather(owner, new OnItemGroupGatherArgs
            { ItemGroupId = 9, ItemId = 123, Count = 4 });
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        quest.CurrentStep.FinalizeStep();
        _questManager.DoItemsAcquiredEvents(owner, first.TemplateId, 4);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    public async Task Drop_UsesOnlyDestroyWhenDropAndOneSharedRemovalBudget(bool cleanup, bool destroyWhenDrop)
    {
        var (owner, first, second) = CreateInventory(9, 7, 6);
        var (quest, _, template) = CreateGatherQuest(owner, 5490, 9, 10, cleanup);
        template.DestroyWhenDrop = destroyWhenDrop;
        quest.Drop(false);
        await Assert.That(first.Count + second.Count).IsEqualTo(destroyWhenDrop ? 3 : 13);
    }

    [Test]
    public async Task Complete_CleanupConsumesOnlyOneGroupBudgetAndKeepsTheExcess()
    {
        var (owner, first, second) = CreateInventory(9, 7, 6);
        var (quest, _, _) = CreateGatherQuest(owner, 5490, 9, 10, true);
        quest.Complete();
        await Assert.That(first.Count).IsEqualTo(0);
        await Assert.That(second.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RemoveQuest_BothCleanupFlags_ConsumesOneGroupBudget(bool complete)
    {
        var previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        try
        {
            var (owner, first, second) = CreateInventory(9, 7, 6);
            owner.Quests = new CharacterQuests(owner, _ => true, _ => { });
            var (quest, _, template) = CreateGatherQuest(owner, 5490, 9, 10, true);
            template.DestroyWhenDrop = true;
            owner.Quests.ActiveQuests.Add(quest.TemplateId, quest);
            if (complete)
                owner.Quests.CompleteQuest(quest.TemplateId);
            else
                owner.Quests.DropQuest(quest.TemplateId, false);
            await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
            await Assert.That(first.Count + second.Count).IsEqualTo(3);
        }
        finally
        {
            AppConfiguration.Instance.DebugInfo = previousDebugInfo;
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ManualDestruction_GroupMemberUsesTheAuthoredDropWhenDestroyFlag(bool dropWhenDestroy)
    {
        var previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        try
        {
            var (owner, first, _) = CreateInventory(9, 4, 0);
            first.Template.LootQuestId = 5490;
            owner.Quests = new CharacterQuests(owner, _ => true, _ => { });
            var (quest, _, template) = CreateGatherQuest(owner, 5490, 9, 10, false);
            template.DropWhenDestroy = dropWhenDestroy;
            owner.Quests.ActiveQuests.Add(quest.TemplateId, quest);
            owner.Inventory.OnItemManuallyDestroyed(first, first.Count);
            await Assert.That(owner.Quests.ActiveQuests.ContainsKey(quest.TemplateId)).IsEqualTo(!dropWhenDestroy);
        }
        finally
        {
            AppConfiguration.Instance.DebugInfo = previousDebugInfo;
        }
    }

    [Test]
    public async Task Gather_RelogRecountsInventoryInsteadOfAddingTheSavedObjectiveAgain()
    {
        var (owner, first, _) = CreateInventory(9, 4, 0);
        var (quest, _, _) = CreateGatherQuest(owner, 5490, 9, 10, false);
        var data = quest.WriteData();
        quest.CurrentStep.FinalizeStep();
        quest.FinalizeQuestActs();
        var (loaded, act, _) = CreateGatherQuest(owner, 5490, 9, 10, false);
        loaded.ReadData(data);
        await Assert.That(act.RunAct()).IsFalse();
        await Assert.That(loaded.Objectives[0]).IsEqualTo(4);
        first.Count += 6;
        owner.Inventory.OnAcquiredItem(first, 6, true);
        await Assert.That(act.RunAct()).IsTrue();
        await Assert.That(loaded.Objectives[0]).IsEqualTo(10);
    }

    [Test]
    public async Task GroupUse_CountsSuccessfulUsesOnlyAndUnsubscribes()
    {
        var (owner, first, second) = CreateInventory(10, 3, 2);
        var questTemplate = new QuestTemplate { Id = 5489 };
        var component = new QuestComponentTemplate(questTemplate) { Id = 23725, KindId = QuestComponentKind.Progress };
        var template = new QuestActObjItemGroupUse(component)
            { ActId = ActId, DetailId = 7, ItemGroupId = 10, Count = 3, ThisComponentObjectiveIndex = 0 };
        component.ActTemplates.Add(template);
        questTemplate.Components.Add(component.Id, component);
        var quest = CreateQuest(questTemplate, owner);
        quest.Step = QuestComponentKind.Progress;
        var act = quest.QuestSteps[QuestComponentKind.Progress].Components[component.Id].Acts.Single();
        await Assert.That(template.CountsAsAnObjective).IsTrue();
        await Assert.That(act.RunAct()).IsFalse();

        _questManager.DoItemsAcquiredEvents(owner, first.TemplateId, 3);
        _questManager.DoItemsConsumedEvents(owner, first.TemplateId, 1);
        owner.ItemUseByTemplate(123);
        owner.Events.OnItemGroupUse(owner, new OnItemGroupUseArgs { ItemGroupId = 10, Count = -1 });
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        owner.ItemUse(first.Id);
        owner.ItemUse(second);
        await Assert.That(quest.Objectives[0]).IsEqualTo(2);
        owner.ItemUseByTemplate(first.TemplateId);
        await Assert.That(act.RunAct()).IsTrue();
        await Assert.That(quest.Objectives[0]).IsEqualTo(3);
        await Assert.That(first.Count + second.Count).IsEqualTo(5);
        quest.CurrentStep.FinalizeStep();
        quest.Objectives[0] = 0;
        owner.ItemUse(first);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
    }

    private (CharacterMock Owner, ItemMock First, ItemMock Second) CreateInventory(uint group, int firstCount, int secondCount)
    {
        var (firstId, secondId) = group switch
        {
            9 => (28557u, 28558u),
            10 => (8518u, 29173u),
            14 => (34602u, 34603u),
            15 => (34619u, 34620u),
            16 => (34636u, 34637u),
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };
        var owner = new CharacterMock { Id = 7, Name = "Questor", NumInventorySlots = 10, NumBankSlots = 10 };
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(owner.Id, slotType, false, owner)
                { ContainerId = (ulong)containers.Count + 1, Owner = owner };
            containers.Add(container.ContainerId, container);
        }
        var allItems = new Dictionary<ulong, Item>();
        SetPrivateField(_itemManager, "_allItems", allItems);
        SetPrivateField(_itemManager, "_removedItems", new List<ulong>());
        SetPrivateField(_itemManager, "_allPersistentContainers", containers);
        SetPrivateField(_questManager, "_groupItems", new Dictionary<uint, List<uint>>
            { [group] = [firstId, secondId, firstId] });
        owner.Inventory = new Inventory(owner);
        ItemMock AddItem(uint templateId, int count, byte slot)
        {
            var item = new ItemMock((uint)slot + 101, new ItemTemplate
                { Id = templateId, MaxCount = 100, BindType = ItemBindType.Normal }, count)
                { OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = slot,
                    _holdingContainer = owner.Inventory.Bag, IsDirty = false };
            owner.Inventory.Bag.Items.Add(item);
            allItems.Add(item.Id, item);
            return item;
        }
        var first = AddItem(firstId, firstCount, 0);
        var second = AddItem(secondId, secondCount, 1);
        owner.Inventory.Bag.UpdateFreeSlotCount();
        return (owner, first, second);
    }

    private (Quest Quest, QuestAct Act, QuestActObjItemGroupGather Template) CreateGatherQuest(
        CharacterMock owner, uint questId, uint groupId, int count, bool cleanup)
    {
        var template = new QuestTemplate { Id = questId };
        var component = new QuestComponentTemplate(template) { Id = 1011, KindId = QuestComponentKind.Progress };
        var gather = new QuestActObjItemGroupGather(component)
            { ActId = ActId, ItemGroupId = groupId, Count = count, Cleanup = cleanup, ThisComponentObjectiveIndex = 0 };
        component.ActTemplates.Add(gather);
        template.Components.Add(component.Id, component);
        var quest = CreateQuest(template, owner);
        quest.Step = QuestComponentKind.Progress;
        return (quest, quest.QuestSteps[QuestComponentKind.Progress].Components[component.Id].Acts.Single(), gather);
    }

    private Quest CreateQuest(QuestTemplate template, CharacterMock owner)
    {
        return new Quest(template, owner, _questManager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object);
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    }
}
