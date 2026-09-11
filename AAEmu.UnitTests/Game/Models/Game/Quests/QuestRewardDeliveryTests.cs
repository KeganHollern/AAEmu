using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestRewardDeliveryTests
{
    private IServiceProvider _previousProvider;
    private bool _previousDebugInfo;
    private readonly Dictionary<FieldInfo, object> _previousSingletons = [];
    private ServiceProvider _provider;
    private ItemManager _items;
    private QuestManager _quests;
    private MailManager _mail;
    private SequenceIds _itemIds;
    private SequenceIds _mailIds;
    private readonly Dictionary<uint, ItemTemplate> _templates = [];

    [Before(Test)]
    public void SetUp()
    {
        _previousProvider = SingletonContainer.ServiceProvider;
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        _itemIds = new SequenceIds();
        _mailIds = new SequenceIds();
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, _itemIds,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        _quests = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        var names = Mock.Of<INameManager>();
        names.GetCharacterName(7).Returns("Questor");
        names.GetCharacterId("Questor").Returns(7u);
        _mail = new MailManager(_mailIds, names.Object, _items, Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object)
        {
            _allPlayerMails = new ConcurrentDictionary<long, BaseMail>()
        };
        _templates.Clear();
        _templates.Add(50_001, new ItemTemplate { Id = 50_001, MaxCount = 1, FixedGrade = 0 });
        _templates.Add(50_002, new ItemTemplate { Id = 50_002, MaxCount = 1, FixedGrade = 0 });
        SetField(_items, "_templates", _templates);
        SetField(_items, "_allItems", new Dictionary<ulong, Item>());
        SetField(_items, "_removedItems", new List<ulong>());
        var services = new ServiceCollection();
        services.AddSingleton(_items);
        services.AddSingleton(_quests);
        services.AddSingleton(_mail);
        _provider = services.BuildServiceProvider();
        foreach (var type in new[] { typeof(ItemManager), typeof(QuestManager), typeof(MailManager) })
        {
            var field = typeof(Singleton<>).MakeGenericType(type).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _previousSingletons.Add(field, field.GetValue(null));
            field.SetValue(null, null);
        }
        SingletonContainer.ServiceProvider = _provider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousProvider;
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
        foreach (var (field, value) in _previousSingletons)
            field.SetValue(null, value);
        _previousSingletons.Clear();
        _provider.Dispose();
    }

    [Test]
    public async Task ValidateRewardReferences_ReportsMissingMandatoryAndSelectiveTemplates()
    {
        var owner = CreateOwner(10);
        var quest = CreateQuest(owner, new ItemCreationDefinition(99_999), new ItemCreationDefinition(50_001));
        var component = quest.Template.Components.Values.Single();
        component.ActTemplates.Add(new QuestActSupplySelectiveItem(component) { ActId = 90, ItemId = 99_998, Count = 1 });
        SetField(_quests, "_questTemplates", new Dictionary<uint, QuestTemplate> { [quest.TemplateId] = (QuestTemplate)quest.Template });
        await Assert.That(_quests.ValidateRewardReferences(_items.GetTemplate)).IsEqualTo(2);
    }

    [Test]
    public async Task RewardStep_InvalidReference_DoesNotRunOtherSideEffectsOrComplete()
    {
        var owner = CreateOwner(10);
        var quest = CreateQuest(owner, new ItemCreationDefinition(99_999));
        var component = quest.Template.Components.Values.Single();
        component.ActTemplates.Insert(0, new QuestActSupplyCopper(component) { ActId = 80, Amount = 500 });
        quest.CreateQuestSteps();
        await Assert.That(quest.RunCurrentStep()).IsFalse();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(quest.QuestRewardCoinsPool).IsEqualTo(0);
        await Assert.That(owner.Money).IsEqualTo(0);
        await Assert.That(quest.AppliedSideEffectActIds.Count).IsEqualTo(0);
        await Assert.That(owner.Quests.HasQuestCompleted(quest.TemplateId)).IsFalse();
    }

    [Test]
    public async Task DirectGrant_NoCapacity_RetainsItemsCurrencyAndExperience()
    {
        var owner = CreateOwner(1);
        var quest = CreateQuest(owner);
        quest.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001, 2));
        quest.QuestRewardCoinsPool = 25;
        quest.QuestRewardExpPool = 75;
        await Assert.That(quest.DistributeRewards(true)).IsFalse();
        await Assert.That(quest.QuestRewardItemsPool.Single().Count).IsEqualTo(2);
        await Assert.That(quest.QuestRewardCoinsPool).IsEqualTo(25);
        await Assert.That(quest.QuestRewardExpPool).IsEqualTo(75);
        await Assert.That(owner.Inventory.Bag.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DirectGrant_LaterDefinitionFails_RetryDoesNotRepeatEarlierGrant()
    {
        var owner = CreateOwner(2);
        var quest = CreateQuest(owner);
        quest.QuestRewardItemsPool.AddRange([new ItemCreationDefinition(50_001, 2), new ItemCreationDefinition(50_002)]);
        await Assert.That(quest.DistributeRewards(true)).IsFalse();
        await Assert.That(owner.Inventory.Bag.Items.Sum(item => item.Count)).IsEqualTo(2);
        await Assert.That(quest.QuestRewardItemsPool.Single().TemplateId).IsEqualTo(50_002u);
        owner.Inventory.Bag.ContainerSize = 3;
        await Assert.That(quest.DistributeRewards(true)).IsTrue();
        await Assert.That(owner.Inventory.Bag.Items.Sum(item => item.Count)).IsEqualTo(3);
        await Assert.That(quest.QuestRewardItemsPool.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DirectGrant_StackCreationFailsAfterOneGrant_RetryOnlyGrantsRemainder()
    {
        var owner = CreateOwner(3);
        var quest = CreateQuest(owner);
        quest.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001, 2));
        _itemIds.FixedId = 100;
        await Assert.That(quest.DistributeRewards(true)).IsFalse();
        await Assert.That(quest.QuestRewardItemsPool.Single().Count).IsEqualTo(1);
        await Assert.That(owner.Inventory.Bag.Items.Sum(item => item.Count)).IsEqualTo(1);
        _itemIds.FixedId = 0;
        await Assert.That(quest.DistributeRewards(true)).IsTrue();
        await Assert.That(owner.Inventory.Bag.Items.Sum(item => item.Count)).IsEqualTo(2);
    }

    [Test]
    public async Task MailPreparation_MissingTemplate_ReturnsFailureWithoutAllocatingAttachments()
    {
        var owner = CreateOwner(0);
        var quest = CreateQuest(owner);
        await Assert.That(_mail.TryCreateQuestRewardMails(owner, quest,
            [new ItemCreationDefinition(50_001), new ItemCreationDefinition(99_999)], out var mails)).IsFalse();
        await Assert.That(mails.Count).IsEqualTo(0);
        await Assert.That(owner.Inventory.MailAttachments.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MailPreparation_PartialCreationFails_CleansAllUnsentAttachments()
    {
        var owner = CreateOwner(0);
        var quest = CreateQuest(owner);
        quest.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001, 2));
        _itemIds.FixedId = 100;
        await Assert.That(quest.DistributeRewards(true)).IsFalse();
        await Assert.That(owner.Inventory.MailAttachments.Items.Count).IsEqualTo(0);
        await Assert.That(quest.QuestRewardItemsPool.Single().Count).IsEqualTo(2);
        await Assert.That(_mail.AllPlayerMails.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MailSend_IdCollision_CleansNewAttachmentsAndRetainsExistingMail()
    {
        var owner = CreateOwner(0);
        var quest = CreateQuest(owner);
        quest.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001));
        var existing = new BaseMail { Id = 100 };
        _mail.AllPlayerMails.Add(100, existing);
        _mailIds.FixedId = 100;
        await Assert.That(quest.DistributeRewards(true)).IsFalse();
        await Assert.That(owner.Inventory.MailAttachments.Items.Count).IsEqualTo(0);
        await Assert.That(quest.QuestRewardItemsPool.Single().Count).IsEqualTo(1);
        await Assert.That(_mail.AllPlayerMails[100]).IsSameReferenceAs(existing);
        await Assert.That(_mailIds.Released.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MailSend_SecondMailFails_RetryDoesNotResendFirstTenItems()
    {
        var owner = CreateOwner(0);
        var quest = CreateQuest(owner);
        quest.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001, 11));
        _mailIds.FixedId = 100;
        await Assert.That(quest.DistributeRewards(true)).IsFalse();
        await Assert.That(_mail.AllPlayerMails.Count).IsEqualTo(1);
        await Assert.That(owner.Inventory.MailAttachments.Items.Sum(item => item.Count)).IsEqualTo(10);
        await Assert.That(quest.QuestRewardItemsPool.Single().Count).IsEqualTo(1);
        _mailIds.FixedId = 0;
        await Assert.That(quest.DistributeRewards(true)).IsTrue();
        await Assert.That(_mail.AllPlayerMails.Count).IsEqualTo(2);
        await Assert.That(owner.Inventory.MailAttachments.Items.Sum(item => item.Count)).IsEqualTo(11);
    }

    [Test]
    public async Task RewardState_SaveReload_PreservesPendingCountsChoiceAndAppliedEffects()
    {
        var owner = CreateOwner(10);
        var original = CreateQuest(owner);
        original.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001, 2, 3));
        original.QuestCleanupItemsPool.Add(new ItemCreationDefinition(50_002, 5));
        original.QuestRewardCoinsPool = 25;
        original.QuestRewardExpPool = 75;
        original.SelectedRewardIndex = 3;
        original.AllowItemRewards = false;
        original.QuestRewardRatio = 0.3;
        original.AppliedSideEffectActIds.Add(42);
        original.AppliedComponentEffectIds.Add(40);
        var loaded = CreateQuest(owner);
        loaded.ReadData(original.WriteData());
        await Assert.That(loaded.QuestRewardItemsPool.Single().Count).IsEqualTo(2);
        await Assert.That(loaded.QuestRewardItemsPool.Single().GradeId).IsEqualTo(3);
        await Assert.That(loaded.QuestCleanupItemsPool.Single().Count).IsEqualTo(5);
        await Assert.That(loaded.QuestRewardCoinsPool).IsEqualTo(25);
        await Assert.That(loaded.QuestRewardExpPool).IsEqualTo(75);
        await Assert.That(loaded.SelectedRewardIndex).IsEqualTo(3);
        await Assert.That(loaded.AllowItemRewards).IsFalse();
        await Assert.That(loaded.QuestRewardRatio).IsEqualTo(0.3);
        await Assert.That(loaded.AppliedSideEffectActIds.Contains(42)).IsTrue();
        await Assert.That(loaded.AppliedComponentEffectIds.Contains(40)).IsTrue();
    }

    [Test]
    public async Task RewardStep_PartialGrantThenReload_CompletesWithoutRepeatingAppliedActs()
    {
        var owner = CreateOwner(2);
        var quest = CreateQuest(owner, new ItemCreationDefinition(50_001, 2), new ItemCreationDefinition(50_002));
        await Assert.That(quest.RunCurrentStep()).IsFalse();
        await Assert.That(quest.AppliedSideEffectActIds.Count).IsEqualTo(2);
        var data = quest.WriteData();
        var loaded = CreateQuest(owner, new ItemCreationDefinition(50_001, 2), new ItemCreationDefinition(50_002));
        loaded.ReadData(data);
        owner.Inventory.Bag.ContainerSize = 3;
        await Assert.That(loaded.RunCurrentStep()).IsTrue();
        await Assert.That(owner.Inventory.Bag.Items.Sum(item => item.Count)).IsEqualTo(3);
        await Assert.That(owner.Quests.HasQuestCompleted(loaded.TemplateId)).IsTrue();
        await Assert.That(loaded.Status).IsEqualTo(QuestStatus.Completed);
        await Assert.That(loaded.RunCurrentStep()).IsFalse();
    }

    [Test]
    public async Task RewardState_LargePendingSet_RoundTripsBeyondTheOldTinyBlobLimit()
    {
        var owner = CreateOwner(0);
        var quest = CreateQuest(owner);
        for (uint index = 0; index < 30; index++)
        {
            quest.QuestRewardItemsPool.Add(new ItemCreationDefinition(50_001 + index, 3));
            quest.AppliedSideEffectActIds.Add(100 + index);
        }
        var data = quest.WriteData();
        await Assert.That(data.Length > 255).IsTrue();
        var loaded = CreateQuest(owner);
        loaded.ReadData(data);
        await Assert.That(loaded.QuestRewardItemsPool.Count).IsEqualTo(30);
        await Assert.That(loaded.AppliedSideEffectActIds.Count).IsEqualTo(30);
    }

    [Test]
    public async Task RankedQuest_NoItemEligibility_DoesNotBlockOnAnUnearnedItem()
    {
        var owner = CreateOwner(10);
        var quest = CreateQuest(owner, new ItemCreationDefinition(99_999));
        quest.AllowItemRewards = false;
        await Assert.That(quest.CurrentStep.RunComponents()).IsTrue();
        await Assert.That(owner.Inventory.Bag.Items.Count).IsEqualTo(0);
        await Assert.That(quest.QuestRewardItemsPool.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReadyToReward_DoesNotMarkCompletedBeforeDelivery()
    {
        var quest = CreateQuest(CreateOwner(10));
        quest.Step = QuestComponentKind.Ready;
        quest.GoToNextStep();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Ready);
    }

    private CharacterMock CreateOwner(int bagSize)
    {
        var owner = new CharacterMock { Id = 7, Name = "Questor", NumInventorySlots = 10, NumBankSlots = 10 };
        var containers = new Dictionary<ulong, ItemContainer>();
        ulong id = 1;
        foreach (var slot in Enum.GetValues<SlotType>().Where(slot => slot != SlotType.EquipmentMate))
        {
            var container = new ItemContainer(owner.Id, slot, false, owner) { ContainerId = id++, Owner = owner };
            containers.Add(container.ContainerId, container);
        }
        SetField(_items, "_allPersistentContainers", containers);
        owner.Inventory = new Inventory(owner);
        owner.Inventory.Bag.ContainerSize = bagSize;
        owner.Quests = new CharacterQuests(owner, _ => true, _ => { });
        return owner;
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task RestartMainQuest_SupplyItem_GrantsMissingCountOnce(int retainedCount)
    {
        var owner = CreateOwner(10);
        var template = new QuestTemplate { Id = 101, DetailId = QuestDetail.Main, RestartOnFail = true };
        var start = new QuestComponentTemplate(template) { Id = 1011, KindId = QuestComponentKind.Start };
        var supply = new QuestComponentTemplate(template) { Id = 1012, KindId = QuestComponentKind.Supply };
        var progress = new QuestComponentTemplate(template) { Id = 1013, KindId = QuestComponentKind.Progress };
        supply.ActTemplates.Add(new QuestActSupplyItem(supply) { ActId = 1020, ItemId = 50001, Count = 1 });
        template.Components.Add(start.Id, start);
        template.Components.Add(supply.Id, supply);
        template.Components.Add(progress.Id, progress);
        var failed = new Quest(template, owner, _quests, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Id = 123,
            Status = QuestStatus.Failed,
            Step = QuestComponentKind.Fail
        };
        failed.AppliedSideEffectActIds.Add(1020);
        owner.Quests.ActiveQuests.Add(template.Id, failed);
        if (retainedCount > 0)
        {
            failed.QuestRewardItemsPool.Add(new ItemCreationDefinition(50001, retainedCount));
            await Assert.That(failed.DistributeRewards(false)).IsTrue();
        }

        var countDuringCommit = -1;
        await Assert.That(owner.Quests.RestartMainQuest(template.Id, _ =>
        {
            countDuringCommit = owner.Inventory.GetItemsCount(50001);
            return true;
        })).IsTrue();
        await Assert.That(countDuringCommit).IsEqualTo(retainedCount);
        var restarted = owner.Quests.ActiveQuests[template.Id];
        await Assert.That(restarted.RunCurrentStep()).IsTrue();
        await Assert.That(restarted.Step).IsEqualTo(QuestComponentKind.Supply);
        await Assert.That(restarted.RunCurrentStep()).IsTrue();
        await Assert.That(owner.Inventory.GetItemsCount(50001)).IsEqualTo(1);
        await Assert.That(restarted.AppliedSideEffectActIds.Contains(1020)).IsTrue();
        await Assert.That(owner.Quests.RestartMainQuest(template.Id, _ => throw new InvalidOperationException("Replay reached persistence"))).IsFalse();
        await Assert.That(failed.RunCurrentStep()).IsFalse();
        await Assert.That(owner.Inventory.GetItemsCount(50001)).IsEqualTo(1);

        var saved = restarted.WriteData();
        restarted.ReadData(saved);
        await Assert.That(restarted.AppliedSideEffectActIds.Contains(1020)).IsTrue();
        await Assert.That(owner.Quests.IsQuestComplete(template.Id)).IsFalse();
        await Assert.That(owner.Money).IsEqualTo(0);
    }

    private Quest CreateQuest(CharacterMock owner, params ItemCreationDefinition[] rewards)
    {
        var template = new QuestTemplate { Id = 1000 };
        var component = new QuestComponentTemplate(template) { Id = 10, KindId = QuestComponentKind.Reward };
        uint actId = 20;
        foreach (var reward in rewards)
            component.ActTemplates.Add(new QuestActSupplyItem(component)
            {
                ActId = actId++, ItemId = reward.TemplateId, Count = reward.Count, GradeId = (byte)Math.Max(0, reward.GradeId)
            });
        template.Components.Add(component.Id, component);
        var quest = new Quest(template, owner, _quests, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Step = QuestComponentKind.Reward, Status = QuestStatus.Ready
        };
        owner.Quests.ActiveQuests[template.Id] = quest;
        return quest;
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class SequenceIds : IItemIdManager, IMailIdManager
    {
        private uint _next = 200;
        public uint FixedId { get; set; }
        public List<uint> Released { get; } = [];
        public void Load() { }
        public bool Initialize(bool forceReset = false) => true;
        public uint GetNextId() => FixedId > 0 ? FixedId : _next++;
        public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => GetNextId()).ToArray();
        public void RetainId(uint id) { }
        public void ReleaseId(uint id) => Released.Add(id);
        public void ReleaseId(IEnumerable<uint> ids) => Released.AddRange(ids);
    }
}
