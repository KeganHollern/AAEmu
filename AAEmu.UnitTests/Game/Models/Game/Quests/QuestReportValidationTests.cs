using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestReportValidationTests
{
    private const uint ItemTemplateId = 50_001;
    private const int InitialItemCount = 3;

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
    private bool _previousDebugInfo;

    [Before(Test)]
    public void SetUp()
    {
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
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
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        s_itemManagerInstanceField.SetValue(null, _previousItemManager);
        s_questManagerInstanceField.SetValue(null, _previousQuestManager);
        _testServiceProvider?.Dispose();
    }


    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 2)]
    [Arguments(true, 0)]
    public async Task SourceLessReport_OrdinaryOrIncompleteQuest_DoesNotAdvance(bool journal, int progress)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, journal ? 1 : 0, QuestComponentKind.Progress, progress);
        _questManager.DoReportEvents(owner, quest.TemplateId, 0, 0, 7);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(0);
        await Assert.That(owner.Quests.HasQuestCompleted(quest.TemplateId)).IsFalse();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SourceLessReport_AuthoredReadyRoute_UsesReadyStep(int reportKind)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, reportKind, QuestComponentKind.Ready, 2);
        _questManager.DoReportEvents(owner, quest.TemplateId, 0, 0, 7);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(7);
        await Assert.That(quest.CurrentStep.Components.Values.Single().RunComponent()).IsTrue();
        await Assert.That(owner.Quests.HasQuestCompleted(quest.TemplateId)).IsFalse();
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, true)]
    public async Task SourceLessReport_LetItDoneJournal_RequiresFullObjectives(int progress, bool accepted)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 1, QuestComponentKind.Progress, progress);
        quest.Template.LetItDone = true;
        await Assert.That(quest.TryReportWithoutSource(0)).IsEqualTo(accepted);
        await Assert.That(quest.Step).IsEqualTo(accepted ? QuestComponentKind.Ready : QuestComponentKind.Progress);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(2, true)]
    public async Task SourceLessReport_LetItDoneWithoutReady_RequiresAuthoredAutomaticReward(int progress, bool accepted)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 2, QuestComponentKind.Progress, progress);
        quest.Template.LetItDone = true;
        var automatic = quest.QuestSteps[QuestComponentKind.Ready].Components.Values.Single().Template;
        quest.Template.Components.Remove(automatic.Id);
        automatic.KindId = QuestComponentKind.Reward;
        quest.Template.Components.Add(automatic.Id, automatic);
        quest.CreateQuestSteps();
        await Assert.That(quest.TryReportWithoutSource(0)).IsEqualTo(accepted);
        await Assert.That(quest.Step).IsEqualTo(accepted ? QuestComponentKind.Reward : QuestComponentKind.Progress);
    }

    [Test]
    [Arguments(QuestComponentKind.Invalid)]
    [Arguments(QuestComponentKind.None)]
    [Arguments(QuestComponentKind.Start)]
    [Arguments(QuestComponentKind.Supply)]
    [Arguments(QuestComponentKind.Fail)]
    [Arguments(QuestComponentKind.Drop)]
    [Arguments(QuestComponentKind.Reward)]
    public async Task SourceLessReport_OutsideReportSteps_DoesNotAdvance(QuestComponentKind step)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 1, step, 2);
        _questManager.DoReportEvents(owner, quest.TemplateId, 0, 0, 7);
        await Assert.That(quest.Step).IsEqualTo(step);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(0);
    }

    [Test]
    public async Task SourceLessReport_ReplayOrStaleInstance_DoesNotChangeSelection()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 1, QuestComponentKind.Ready, 2);
        await Assert.That(quest.TryReportWithoutSource(7)).IsTrue();
        await Assert.That(quest.TryReportWithoutSource(8)).IsFalse();
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(7);
        owner.Quests.ActiveQuests.Remove(quest.TemplateId);
        await Assert.That(quest.TryReportWithoutSource(9)).IsFalse();
        _questManager.DoReportEvents(owner, quest.TemplateId, 0, 0, 9);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(7);
    }

    [Test]
    [Arguments(-1, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    public async Task SourceLessReport_SelectiveReward_RequiresAuthoredIndex(int selected, bool accepted)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 1, QuestComponentKind.Ready, 2, selective: true);
        await Assert.That(quest.TryReportWithoutSource(selected)).IsEqualTo(accepted);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task SelectiveReward_InactiveComponent_CannotBeSelected()
    {
        var owner = CreateOwnerWithItems();
        owner.Level = 1;
        var quest = CreateQuest(owner, 1, QuestComponentKind.Ready, 2, selective: true);
        var requirements = UnitRequirementsGameData.Instance;
        var property = typeof(UnitRequirementsGameData).GetProperty("_unitReqsByOwnerType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = property.GetValue(requirements);
        try
        {
            property.SetValue(requirements, new Dictionary<string, List<UnitReqs>>
            {
                ["QuestComponent"] = [new UnitReqs { OwnerId = 555_552, KindType = UnitReqsKindType.Level, Value1 = 50 }]
            });
            await Assert.That(quest.IsValidSelectedRewardIndex(0)).IsFalse();
            await Assert.That(quest.TryReportWithoutSource(0)).IsFalse();
            owner.Level = 50;
            await Assert.That(quest.IsValidSelectedRewardIndex(0)).IsTrue();
            await Assert.That(quest.TryReportWithoutSource(0)).IsTrue();
        }
        finally
        {
            property.SetValue(requirements, previous);
        }
    }

    [Test]
    public async Task SourceLessReport_ReadyNpcQuest_CannotBypassReport()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Ready, 2);
        owner.CurrentTarget = new Npc { TemplateId = 15 };
        _questManager.DoReportEvents(owner, quest.TemplateId, 0, 0, 7);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(0);
        await Assert.That(quest.CurrentStep.Components.Values.Single().RunComponent()).IsFalse();
    }

    [Test]
    public async Task JournalReady_RequiresReportBeforeRunning()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 1, QuestComponentKind.Ready, 2);
        await Assert.That(quest.CurrentStep.Components.Values.Single().RunComponent()).IsFalse();
        await Assert.That(quest.TryReportWithoutSource(0)).IsTrue();
        await Assert.That(quest.CurrentStep.Components.Values.Single().RunComponent()).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadyRemoval_RequiresReportBeforeConsuming(bool removalFirst)
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Ready, 2);
        var component = quest.CurrentStep.Components.Values.Single();
        AddRemoval(component, ItemTemplateId, 2, removalFirst);
        await Assert.That(component.RunComponent()).IsFalse();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(3);
        component.Acts.Single(act => act.Template is QuestActConReportNpc).OverrideObjectiveCompleted = true;
        await Assert.That(component.RunComponent()).IsTrue();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(1);
        await Assert.That(component.RunComponent()).IsTrue();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task ReadyRemoval_InsufficientFullCount_LeavesItemsAndQuestIntact()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Ready, 2);
        var component = quest.CurrentStep.Components.Values.Single();
        AddRemoval(component, ItemTemplateId, 4);
        component.Acts[0].OverrideObjectiveCompleted = true;
        await Assert.That(component.RunComponent()).IsFalse();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(3);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task ReadyRemoval_MultipleRequirements_MissingItemDoesNotConsumeEarlierItem()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Ready, 2);
        var component = quest.CurrentStep.Components.Values.Single();
        AddRemoval(component, ItemTemplateId, 1);
        AddRemoval(component, ItemTemplateId + 1, 1);
        AddRemoval(component, ItemTemplateId + 2, 1);
        component.Acts[0].OverrideObjectiveCompleted = true;
        await Assert.That(component.RunComponent()).IsFalse();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(3);
    }

    [Test]
    public async Task ReadyCopper_ReportIsMandatoryAndRepeatedEvaluationDoesNotGrantTwice()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Ready, 2);
        var component = quest.CurrentStep.Components.Values.Single();
        component.Acts.Add(new QuestAct(component, new QuestActSupplyCopper(component.Template) { ActId = 105, Amount = 45 }));
        await Assert.That(component.RunComponent()).IsFalse();
        await Assert.That(quest.QuestRewardCoinsPool).IsEqualTo(0);
        component.Acts[0].OverrideObjectiveCompleted = true;
        await Assert.That(component.RunComponent()).IsTrue();
        await Assert.That(component.RunComponent()).IsTrue();
        await Assert.That(quest.QuestRewardCoinsPool).IsEqualTo(45);
    }

    [Test]
    public async Task ProgressRemoval_WaitsForTalkObjective()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Progress, 0);
        var component = quest.CurrentStep.Components.Values.Single();
        component.Acts[0].Template = new QuestActObjTalk(component.Template) { Count = 1, ThisComponentObjectiveIndex = 0 };
        AddRemoval(component, ItemTemplateId, 1);
        await Assert.That(component.RunComponent()).IsFalse();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(3);
        quest.Objectives[0] = 1;
        await Assert.That(component.RunComponent()).IsTrue();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(2);
    }

    [Test]
    public async Task ReadyReportAlternatives_OneMatchingReportStillCompletesComponent()
    {
        var owner = CreateOwnerWithItems();
        var quest = CreateQuest(owner, 0, QuestComponentKind.Ready, 2);
        var component = quest.CurrentStep.Components.Values.Single();
        var second = new QuestAct(component, new QuestActConReportNpc(component.Template) { NpcId = 99 });
        component.Acts.Add(second);
        second.OverrideObjectiveCompleted = true;
        await Assert.That(component.RunComponent()).IsTrue();
    }

    private static void AddRemoval(QuestComponent component, uint itemId, int count, bool first = false)
    {
        var act = new QuestAct(component, new QuestActSupplyRemoveItem(component.Template) { ActId = (uint)(100 + component.Acts.Count), ItemId = itemId, Count = count });
        component.Acts.Insert(first ? 0 : component.Acts.Count, act);
    }

    private Quest CreateQuest(CharacterMock owner, int reportKind, QuestComponentKind step, int progress, bool selective = false)
    {
        var template = new QuestTemplate { Id = 55_555 };
        var objective = new QuestComponentTemplate(template) { Id = 555_550, KindId = QuestComponentKind.Progress };
        objective.ActTemplates.Add(new QuestActObjMonsterHunt(objective) { ActId = 1, Count = 2, ThisComponentObjectiveIndex = 0 });
        var ready = new QuestComponentTemplate(template) { Id = 555_551, KindId = QuestComponentKind.Ready };
        ready.ActTemplates.Add(reportKind switch
        {
            1 => new QuestActConReportJournal(ready) { ActId = 2 },
            2 => new QuestActConAutoComplete(ready) { ActId = 2 },
            _ => new QuestActConReportNpc(ready) { ActId = 2, NpcId = 15 }
        });
        template.Components.Add(objective.Id, objective);
        template.Components.Add(ready.Id, ready);
        if (selective)
        {
            var reward = new QuestComponentTemplate(template) { Id = 555_552, KindId = QuestComponentKind.Reward };
            reward.ActTemplates.Add(new QuestActSupplySelectiveItem(reward) { ActId = 300, ThisSelectiveIndex = 0, ItemId = 500 });
            reward.ActTemplates.Add(new QuestActSupplySelectiveItem(reward) { ActId = 301, ThisSelectiveIndex = 1, ItemId = 501 });
            template.Components.Add(reward.Id, reward);
        }
        var quest = new Quest(template, owner, _questManager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object);
        quest.Objectives[0] = progress;
        quest.Step = step;
        owner.Quests.ActiveQuests.Add(template.Id, quest);
        return quest;
    }

    private CharacterMock CreateOwnerWithItems()
    {
        var owner = new CharacterMock
        {
            Id = 7,
            Name = "Questor",
            NumInventorySlots = 10,
            NumBankSlots = 10
        };

        var containers = new Dictionary<ulong, ItemContainer>();
        ulong containerId = 1;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;

            var container = new ItemContainer(owner.Id, slotType, false, owner)
            {
                ContainerId = containerId++,
                Owner = owner
            };
            containers.Add(container.ContainerId, container);
        }

        var allItems = new Dictionary<ulong, Item>();
        SetPrivateField(_itemManager, "_allItems", allItems);
        SetPrivateField(_itemManager, "_removedItems", new List<ulong>());
        SetPrivateField(_itemManager, "_allPersistentContainers", containers);

        owner.Inventory = new Inventory(owner);
        owner.Quests = new CharacterQuests(owner, _ => true, _ => { });

        var item = new ItemMock(
            101,
            new ItemTemplate
            {
                Id = ItemTemplateId,
                MaxCount = 100,
                BindType = ItemBindType.Normal
            },
            InitialItemCount)
        {
            OwnerId = owner.Id,
            SlotType = SlotType.Inventory,
            Slot = 0,
            _holdingContainer = owner.Inventory.Bag,
            IsDirty = false
        };
        owner.Inventory.Bag.Items.Add(item);
        owner.Inventory.Bag.UpdateFreeSlotCount();
        allItems.Add(item.Id, item);
        return owner;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }
}
