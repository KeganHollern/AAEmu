using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
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

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class QuestItemGainStarterTests
{
    private const uint QuestId = 101;
    private const uint OtherQuestId = 102;
    private const uint ItemTemplateId = 50_001;
    private const uint AlternativeItemTemplateId = 50_002;

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
    public async Task DoItemsAcquiredEvents_PartialCount_DoesNotStartQuest()
    {
        RegisterTemplate(CreateTemplate(QuestId, false, (ItemTemplateId, 5)));
        var (owner, _) = CreateOwnerWithItems(7, (ItemTemplateId, 4));

        _questManager.DoItemsAcquiredEvents(owner, ItemTemplateId, 4);

        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(_questManager.GetItemGainQuestStarters(owner, ItemTemplateId)).IsEmpty();
    }

    [Test]
    public async Task GetItemGainQuestStarters_ExistingStackCrossesThreshold_SelectsStarter()
    {
        var template = CreateTemplate(QuestId, false, (ItemTemplateId, 5));
        RegisterTemplate(template);
        var (owner, items) = CreateOwnerWithItems(7, (ItemTemplateId, 4));

        items[ItemTemplateId].Count += 1;
        var starters = _questManager.GetItemGainQuestStarters(owner, ItemTemplateId);

        await Assert.That(starters).HasSingleItem();
        await Assert.That(starters[0].ParentQuestTemplate).IsSameReferenceAs(template);
    }

    [Test]
    public async Task GetItemGainQuestStarters_PartialInventoryAfterRelog_UsesRestoredTotal()
    {
        RegisterTemplate(CreateTemplate(QuestId, false, (ItemTemplateId, 5)));
        var (reloggedOwner, items) = CreateOwnerWithItems(8, (ItemTemplateId, 3));

        await Assert.That(_questManager.GetItemGainQuestStarters(reloggedOwner, ItemTemplateId)).IsEmpty();

        items[ItemTemplateId].Count += 2;
        var starters = _questManager.GetItemGainQuestStarters(reloggedOwner, ItemTemplateId);

        await Assert.That(starters).HasSingleItem();
        await Assert.That(starters[0].ParentQuestTemplate.Id).IsEqualTo(QuestId);
    }

    [Test]
    public async Task GetItemGainQuestStarters_RestoredActiveQuest_DoesNotSelectDuplicate()
    {
        var template = CreateTemplate(QuestId, false, (ItemTemplateId, 1));
        RegisterTemplate(template);
        var (firstOwner, _) = CreateOwnerWithItems(7, (ItemTemplateId, 1));
        var acceptedQuest = CreateQuest(template, firstOwner);
        acceptedQuest.Step = QuestComponentKind.Start;
        acceptedQuest.QuestAcceptorType = QuestAcceptorType.Item;
        acceptedQuest.AcceptorId = ItemTemplateId;
        var savedData = acceptedQuest.WriteData();

        var (reloggedOwner, _) = CreateOwnerWithItems(8, (ItemTemplateId, 1));
        var restoredQuest = CreateQuest(template, reloggedOwner);
        restoredQuest.ReadData(savedData);
        reloggedOwner.Quests.ActiveQuests.Add(template.Id, restoredQuest);

        await Assert.That(restoredQuest.QuestAcceptorType).IsEqualTo(QuestAcceptorType.Item);
        await Assert.That(restoredQuest.AcceptorId).IsEqualTo(ItemTemplateId);
        await Assert.That(_questManager.GetItemGainQuestStarters(reloggedOwner, ItemTemplateId)).IsEmpty();
    }

    [Test]
    public async Task GetItemGainQuestStarters_CompletedQuests_SelectsOnlyRepeatableQuest()
    {
        var nonRepeatable = CreateTemplate(QuestId, false, (ItemTemplateId, 1));
        var repeatable = CreateTemplate(OtherQuestId, true, (ItemTemplateId, 1));
        RegisterTemplate(nonRepeatable);
        RegisterTemplate(repeatable);
        var (owner, _) = CreateOwnerWithItems(7, (ItemTemplateId, 1));
        owner.Quests.SetCompletedQuestFlag(QuestId, true, _ => true, out _, out _);
        owner.Quests.SetCompletedQuestFlag(OtherQuestId, true, _ => true, out _, out _);

        var starters = _questManager.GetItemGainQuestStarters(owner, ItemTemplateId);

        await Assert.That(starters).HasSingleItem();
        await Assert.That(starters[0].ParentQuestTemplate).IsSameReferenceAs(repeatable);
    }

    [Test]
    public async Task GetItemGainQuestStarters_AlternativeItems_SelectSameQuestWithMatchingAcceptor()
    {
        var template = CreateTemplate(
            QuestId,
            false,
            (ItemTemplateId, 1),
            (AlternativeItemTemplateId, 1));
        RegisterTemplate(template);
        var (owner, _) = CreateOwnerWithItems(7, (AlternativeItemTemplateId, 1));

        var starters = _questManager.GetItemGainQuestStarters(owner, AlternativeItemTemplateId);
        var quest = CreateQuest(template, owner);
        quest.QuestAcceptorType = QuestAcceptorType.Item;
        quest.AcceptorId = AlternativeItemTemplateId;
        var startActs = quest.QuestSteps[QuestComponentKind.Start]
            .Components.Values
            .SelectMany(component => component.Acts)
            .ToArray();

        await Assert.That(starters).HasSingleItem();
        await Assert.That(starters[0].ParentQuestTemplate).IsSameReferenceAs(template);
        await Assert.That(startActs.Single(act => act.Template is QuestActConAcceptItemGain gain && gain.ItemId == ItemTemplateId).RunAct()).IsFalse();
        await Assert.That(startActs.Single(act => act.Template is QuestActConAcceptItemGain gain && gain.ItemId == AlternativeItemTemplateId).RunAct()).IsTrue();
    }

    [Test]
    public async Task GetItemGainQuestStarters_SharedItem_SelectsEveryQuestTemplate()
    {
        RegisterTemplate(CreateTemplate(QuestId, false, (ItemTemplateId, 1)));
        RegisterTemplate(CreateTemplate(OtherQuestId, false, (ItemTemplateId, 1)));
        var (owner, _) = CreateOwnerWithItems(7, (ItemTemplateId, 1));

        var questIds = _questManager.GetItemGainQuestStarters(owner, ItemTemplateId)
            .Select(starter => starter.ParentQuestTemplate.Id)
            .ToArray();

        await Assert.That(questIds).IsEquivalentTo(new[] { QuestId, OtherQuestId });
    }

    [Test]
    public async Task DoItemsConsumedEvents_SufficientRemainingCount_DoesNotStartQuest()
    {
        RegisterTemplate(CreateTemplate(QuestId, false, (ItemTemplateId, 1)));
        var (owner, _) = CreateOwnerWithItems(7, (ItemTemplateId, 2));

        _questManager.DoItemsConsumedEvents(owner, ItemTemplateId, 1);

        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
    }

    private void RegisterTemplate(QuestTemplate template)
    {
        var questTemplates = GetPrivateField<Dictionary<uint, QuestTemplate>>(_questManager, "_questTemplates");
        questTemplates.Add(template.Id, template);

        var actsByType = GetPrivateField<Dictionary<string, Dictionary<uint, QuestActTemplate>>>(
            _questManager,
            "_actTemplatesByDetailType");
        if (!actsByType.TryGetValue(nameof(QuestActConAcceptItemGain), out var itemGainActs))
        {
            itemGainActs = [];
            actsByType.Add(nameof(QuestActConAcceptItemGain), itemGainActs);
        }

        foreach (var act in template.GetComponents(QuestComponentKind.Start)
                     .SelectMany(component => component.ActTemplates)
                     .OfType<QuestActConAcceptItemGain>())
            itemGainActs.Add(act.DetailId, act);
    }

    private static QuestTemplate CreateTemplate(
        uint questId,
        bool repeatable,
        params (uint ItemId, int Count)[] itemGainDefinitions)
    {
        var template = new QuestTemplate
        {
            Id = questId,
            Repeatable = repeatable
        };
        var startComponent = new QuestComponentTemplate(template)
        {
            Id = questId * 10 + 1,
            KindId = QuestComponentKind.Start
        };
        for (var index = 0; index < itemGainDefinitions.Length; index++)
        {
            var definition = itemGainDefinitions[index];
            startComponent.ActTemplates.Add(new QuestActConAcceptItemGain(startComponent)
            {
                ActId = questId * 100 + (uint)index + 1,
                DetailId = questId * 100 + (uint)index + 1,
                ItemId = definition.ItemId,
                Count = definition.Count
            });
        }
        template.Components.Add(startComponent.Id, startComponent);
        return template;
    }

    private (CharacterMock Owner, Dictionary<uint, ItemMock> Items) CreateOwnerWithItems(
        uint ownerId,
        params (uint ItemId, int Count)[] itemDefinitions)
    {
        var owner = new CharacterMock
        {
            Id = ownerId,
            Name = $"Questor {ownerId}",
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
        owner.Quests = new CharacterQuests(owner);

        var items = new Dictionary<uint, ItemMock>();
        ulong itemId = 100;
        foreach (var definition in itemDefinitions)
        {
            var item = new ItemMock(
                (uint)itemId++,
                new ItemTemplate
                {
                    Id = definition.ItemId,
                    MaxCount = 1_000,
                    BindType = ItemBindType.Normal
                },
                definition.Count)
            {
                OwnerId = owner.Id,
                SlotType = SlotType.Inventory,
                Slot = (int)items.Count,
                _holdingContainer = owner.Inventory.Bag,
                IsDirty = false
            };
            owner.Inventory.Bag.Items.Add(item);
            allItems.Add(item.Id, item);
            items.Add(item.TemplateId, item);
        }
        owner.Inventory.Bag.UpdateFreeSlotCount();
        return (owner, items);
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

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        return (T)instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }
}
