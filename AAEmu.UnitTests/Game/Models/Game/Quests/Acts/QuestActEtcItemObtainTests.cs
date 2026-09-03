using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
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
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

[NotInParallel]
public sealed class QuestActEtcItemObtainTests
{
    private const uint ItemTemplateId = 50_001;
    private const uint OtherItemTemplateId = 50_002;
    private const uint ObtainActId = 1_012;
    private const uint OtherObtainActId = 1_013;

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
    public async Task OnItemGather_AfterAcceptance_TracksOnlyMatchingPositiveAcquisitionsOnce()
    {
        var owner = CreateOwner(7);
        var template = CreateObtainTemplate((ObtainActId, ItemTemplateId, 4, false));
        var quest = CreateQuest(template, owner);
        var act = GetObtainAct(quest, ObtainActId);

        _questManager.DoItemsAcquiredEvents(owner, ItemTemplateId, 4);
        await Assert.That(act.RunAct()).IsFalse();

        quest.Step = QuestComponentKind.Supply;
        _questManager.DoItemsAcquiredEvents(owner, OtherItemTemplateId, 4);
        _questManager.DoItemsConsumedEvents(owner, ItemTemplateId, 4);
        _questManager.DoItemsAcquiredEvents(owner, ItemTemplateId, 1);

        quest.Step = QuestComponentKind.Progress;
        _questManager.DoItemsAcquiredEvents(owner, ItemTemplateId, 2);
        await Assert.That(act.RunAct()).IsFalse();

        _questManager.DoItemsAcquiredEvents(owner, ItemTemplateId, 1);
        await Assert.That(act.RunAct()).IsTrue();
    }

    [Test]
    public async Task ReadData_PartialIndependentProgress_ContinuesAfterRelog()
    {
        var template = CreateObtainTemplate(
            (ObtainActId, ItemTemplateId, 3, false),
            (OtherObtainActId, OtherItemTemplateId, 2, false));
        var firstOwner = CreateOwner(7);
        var firstQuest = CreateQuest(template, firstOwner);
        firstQuest.Step = QuestComponentKind.Progress;

        _questManager.DoItemsAcquiredEvents(firstOwner, ItemTemplateId, 2);
        _questManager.DoItemsAcquiredEvents(firstOwner, OtherItemTemplateId, 1);
        var savedData = firstQuest.WriteData();
        firstQuest.FinalizeQuestActs();

        var reloggedOwner = CreateOwner(8);
        var reloggedQuest = CreateQuest(template, reloggedOwner);
        reloggedQuest.ReadData(savedData);
        var firstAct = GetObtainAct(reloggedQuest, ObtainActId);
        var secondAct = GetObtainAct(reloggedQuest, OtherObtainActId);

        await Assert.That(firstAct.RunAct()).IsFalse();
        await Assert.That(secondAct.RunAct()).IsFalse();

        _questManager.DoItemsAcquiredEvents(reloggedOwner, ItemTemplateId, 1);
        _questManager.DoItemsConsumedEvents(reloggedOwner, OtherItemTemplateId, 1);
        await Assert.That(firstAct.RunAct()).IsTrue();
        await Assert.That(secondAct.RunAct()).IsFalse();

        _questManager.DoItemsAcquiredEvents(reloggedOwner, OtherItemTemplateId, 1);
        await Assert.That(secondAct.RunAct()).IsTrue();
    }

    [Test]
    public async Task QuestCleanup_ExistingStack_CreditsOnlyAcquiredDeltaAndKeepsHistoricalProgress()
    {
        var (owner, item) = CreateOwnerWithItem(2);
        var template = CreateObtainTemplate((ObtainActId, ItemTemplateId, 4, true));
        var quest = CreateQuest(template, owner);
        quest.Step = QuestComponentKind.Progress;
        var act = GetObtainAct(quest, ObtainActId);

        item.Count += 3;
        owner.Inventory.OnAcquiredItem(item, 3, true);

        await Assert.That(act.RunAct()).IsFalse();
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(5);

        owner.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, ItemTemplateId, 1, item);
        await Assert.That(act.RunAct()).IsFalse();

        item.Count += 1;
        owner.Inventory.OnAcquiredItem(item, 1, true);
        await Assert.That(act.RunAct()).IsTrue();

        quest.Cleanup();

        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(1);
        await Assert.That(act.RunAct()).IsTrue();
    }

    [Test]
    public async Task GetQuestObjectiveStatus_LetItDone_RemainsNotReadyUntilHiddenRequirementsAreMet()
    {
        var owner = CreateOwner(7);
        var template = CreateObtainTemplate(
            (ObtainActId, ItemTemplateId, 1, false),
            (OtherObtainActId, OtherItemTemplateId, 1, false));
        template.LetItDone = true;
        var progressComponent = template.GetFirstComponent(QuestComponentKind.Progress);
        progressComponent.ActTemplates.Insert(0, new QuestActObjMonsterHunt(progressComponent)
        {
            ActId = 1_011,
            Count = 4,
            NpcId = 60_001,
            ThisComponentObjectiveIndex = 0
        });
        var quest = CreateQuest(template, owner);
        quest.Step = QuestComponentKind.Progress;
        quest.Objectives[0] = 2;

        await Assert.That(quest.GetQuestObjectiveStatus()).IsEqualTo(QuestObjectiveStatus.NotReady);

        _questManager.DoItemsAcquiredEvents(owner, ItemTemplateId, 1);
        await Assert.That(quest.GetQuestObjectiveStatus()).IsEqualTo(QuestObjectiveStatus.NotReady);

        _questManager.DoItemsAcquiredEvents(owner, OtherItemTemplateId, 1);
        await Assert.That(quest.GetQuestObjectiveStatus()).IsEqualTo(QuestObjectiveStatus.CanEarlyComplete);
    }

    private static CharacterMock CreateOwner(uint id)
    {
        return new CharacterMock
        {
            Id = id,
            Name = $"Questor {id}"
        };
    }

    private (CharacterMock Owner, ItemMock Item) CreateOwnerWithItem(int itemCount)
    {
        var owner = CreateOwner(7);
        owner.NumInventorySlots = 10;
        owner.NumBankSlots = 10;

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

        var item = new ItemMock(
            101,
            new ItemTemplate
            {
                Id = ItemTemplateId,
                MaxCount = 100,
                BindType = ItemBindType.Normal
            },
            itemCount)
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
        return (owner, item);
    }

    private static QuestTemplate CreateObtainTemplate(
        params (uint ActId, uint ItemId, int Count, bool Cleanup)[] definitions)
    {
        var template = new QuestTemplate { Id = 101 };
        var progressComponent = new QuestComponentTemplate(template)
        {
            Id = 1_011,
            KindId = QuestComponentKind.Progress
        };
        foreach (var definition in definitions)
        {
            progressComponent.ActTemplates.Add(new QuestActEtcItemObtain(progressComponent)
            {
                ActId = definition.ActId,
                ItemId = definition.ItemId,
                Count = definition.Count,
                Cleanup = definition.Cleanup
            });
        }
        template.Components.Add(progressComponent.Id, progressComponent);
        return template;
    }

    private Quest CreateQuest(QuestTemplate template, CharacterMock owner)
    {
        return new Quest(
            template,
            owner,
            _questManager,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
    }

    private static QuestAct GetObtainAct(Quest quest, uint actId)
    {
        return quest.QuestSteps[QuestComponentKind.Progress]
            .Components.Values
            .SelectMany(component => component.Acts)
            .Single(act => act.Id == actId);
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }
}
