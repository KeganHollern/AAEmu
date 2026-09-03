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

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestActConAcceptItemTests
{
    private const uint ItemTemplateId = 50_001;
    private const int InitialItemCount = 2;

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
    public async Task QuestCleanup_CleanupEnabled_ConsumesExactlyOneStarterItem()
    {
        var owner = CreateOwnerWithItems();
        var act = CreateAct(owner, out var quest);
        act.Cleanup = true;

        act.QuestCleanup(quest);

        await Assert.That(act.Count).IsEqualTo(1);
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(InitialItemCount - 1);
    }

    [Test]
    public async Task QuestDropped_DestroyWhenDropEnabled_ConsumesExactlyOneStarterItem()
    {
        var owner = CreateOwnerWithItems();
        var act = CreateAct(owner, out var quest);
        act.DestroyWhenDrop = true;

        act.QuestDropped(quest);

        await Assert.That(act.Count).IsEqualTo(1);
        await Assert.That(owner.Inventory.GetItemsCount(ItemTemplateId)).IsEqualTo(InitialItemCount - 1);
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

    private QuestActConAcceptItem CreateAct(CharacterMock owner, out Quest quest)
    {
        var template = new QuestTemplate { Id = 101 };
        var startComponent = new QuestComponentTemplate(template)
        {
            Id = 1_011,
            KindId = QuestComponentKind.Start
        };
        var act = new QuestActConAcceptItem(startComponent)
        {
            ActId = 1_012,
            ItemId = ItemTemplateId
        };
        startComponent.ActTemplates.Add(act);
        template.Components.Add(startComponent.Id, startComponent);

        quest = new Quest(
            template,
            owner,
            _questManager,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object)
        {
            QuestAcceptorType = QuestAcceptorType.Item,
            AcceptorId = ItemTemplateId
        };
        return act;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }
}
