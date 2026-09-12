using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

[NotInParallel]
public sealed class WorldEconomySettlementTests
{
    private readonly Dictionary<Type, object> _singletons = [];
    private ItemManager _items;
    private MailManager _mail;
    private SpecialtyManager _specialty;
    private Mock<ISaveManager> _save;
    private CharacterMock _seller;
    private Item _pack;
    private Npc _trader;
    private Dictionary<ulong, Item> _allItems;
    private Dictionary<uint, ItemTemplate> _templates;
    private Dictionary<ulong, ItemContainer> _containers;
    private Dictionary<uint, SpecialtyNpc> _traders;
    private Dictionary<(uint, uint), Specialty> _routes;
    private uint _nextItem;
    private uint _nextMail;
    private FeatureSet _previousFeatures;
    private WorldConfig _worldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _nextItem = 1000;
        _nextMail = 10000;
        _previousFeatures = FeaturesManager.Fsets;
        typeof(FeaturesManager).GetProperty(nameof(FeaturesManager.Fsets))!.SetValue(null, new FeatureSet());
        FeaturesManager.Fsets.Set(Feature.backpackProfitShare, false);
        _worldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 7 };
        var names = new NameManager();
        names.Load([], [], []);
        ReplaceSingleton(names);
        ReplaceSingleton(new LocalizationManager());
        ReplaceSingleton(new AccountManager(Mock.Of<ITickManager>().Object, Mock.Of<ITimedRewardsManager>().Object, TimeProvider.System));
        ReplaceSingleton(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
        ReplaceSingleton(formulas);
        var itemIds = Mock.Of<IItemIdManager>();
        itemIds.GetNextId().Returns(() => _nextItem++);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, itemIds.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        ReplaceSingleton(_items);
        _allItems = [];
        _templates = [];
        _containers = [];
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_allPersistentContainers", _containers);
        SetField(_items, "_removedItems", new List<ulong>());
        SetField(_items, "_templates", _templates);
        SetField(_items, "_grades", new Dictionary<int, GradeTemplate> { [0] = new() { RefundMultiplier = 100 } });
        _seller = new CharacterMock { Id = 1, AccountId = 10, Name = "Seller", Money = 1000, NumInventorySlots = 20 };
        names.AddCharacter(1, "Seller", 10);
        _seller.InitializeLaborCache(100, DateTime.UtcNow);
        foreach (var slot in Enum.GetValues<SlotType>().Where(slot => slot != SlotType.EquipmentMate))
        {
            var container = new ItemContainer(1, slot, false, _seller) { Owner = _seller, ContainerId = (ulong)_containers.Count + 1 };
            _containers.Add(container.ContainerId, container);
        }
        _seller.Inventory = new Inventory(_seller);
        _seller.Mails = new CharacterMails(_seller);
        _seller.Actability = new CharacterActability(_seller);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(new Dictionary<uint, WorldInstance> { [1] = world }));
        ReplaceSingleton(worldManager);
        _seller.ParentWorld = world;
        SetField(_seller.Transform, "_zoneId", 100u);
        _trader = new Npc { ObjId = 20, TemplateId = 200, ParentWorld = world, Template = new NpcTemplate { Specialty = true } };
        SetField(_trader.Transform, "_zoneId", 200u);
        world.SetNpc(20, _trader);
        var zones = Mock.Of<IZoneManager>();
        zones.GetZoneByKey(100).Returns(new Zone { GroupId = 3 });
        zones.GetZoneByKey(200).Returns(new Zone { GroupId = 2 });
        var mailIds = Mock.Of<IMailIdManager>();
        mailIds.GetNextId().Returns(() => _nextMail++);
        _mail = new MailManager(mailIds.Object, names, _items, Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object) { _allPlayerMails = [] };
        SetField(_mail, "_deletedMailIds", new List<long>());
        _save = Mock.Of<ISaveManager>();
        _specialty = new SpecialtyManager(_items, _mail, zones.Object, Mock.Of<ITaskManager>().Object,
            new Lazy<ISaveManager>(() => _save.Object));
        _traders = new() { [200] = new() { NpcId = 200, SpecialtyBundleId = 7 } };
        SetField(_specialty, "_specialtyNpc", _traders);
        SetField(_specialty, "_specialtyBundleItemsMapped", new Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>>
            { [42] = new() { [7] = new() { ItemId = 42, Profit = 1000, Ratio = 1000 } } });
        _routes = new() { [(1, 2)] = new() { RowZoneGroupId = 1, ColZoneGroupId = 2, Profit = 2000, Ratio = 1500 } };
        SetField(_specialty, "_routes", _routes);
        _templates[42] = new BackpackTemplate { Id = 42, MaxCount = 1, SpecialtyZoneId = 1, NormalSpeciality = true, Refund = 100 };
        _pack = AddItem(_seller.Inventory.Equipment, 42, 1);
        _pack.Slot = (int)EquipmentItemSlot.Backpack;
        _pack.IsDirty = false;
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _worldConfig;
        typeof(FeaturesManager).GetProperty(nameof(FeaturesManager.Fsets))!.SetValue(null, _previousFeatures);
        foreach (var (type, previous) in _singletons)
            typeof(Singleton<>).MakeGenericType(type).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    public async Task Sale_SaveFailure_RestoresPackLaborAndDemand_AndRemovesPayout()
    {
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(false);
        await Assert.That(_specialty.SellSpecialty(_seller, 20)).IsEqualTo(0);
        await Assert.That(_pack.Count).IsEqualTo(1);
        await Assert.That(_pack.IsDirty).IsFalse();
        await Assert.That(_seller.LaborPower).IsEqualTo((short)100);
        await Assert.That(_seller.ConsumedLaborPower).IsEqualTo(0);
        await Assert.That(_mail._allPlayerMails).IsEmpty();
        await Assert.That(Demand).IsEmpty();
    }

    [Test]
    public async Task Sale_CommitsOnePackAndLaborDebit_AndUsesTraderZoneDemand()
    {
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);
        await Assert.That(_specialty.SellSpecialty(_seller, 20)).IsEqualTo(1100);
        await Assert.That(_specialty.SellSpecialty(_seller, 20)).IsEqualTo(0);
        await Assert.That(_seller.LaborPower).IsEqualTo((short)40);
        await Assert.That(_seller.ConsumedLaborPower).IsEqualTo(60);
        await Assert.That(_mail._allPlayerMails.Values.Single().Body.CopperCoins).IsEqualTo(1502);
        await Assert.That(Demand[(42, 2)].PendingSales).IsEqualTo(1);
        await Assert.That(Demand.ContainsKey((42, 3))).IsFalse();
    }

    [Test]
    public async Task Sale_CommitException_KeepsPackAndOutputsPrepared()
    {
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Throws(new IOException("Unknown commit outcome"));
        await Assert.That(() => _specialty.SellSpecialty(_seller, 20)).Throws<IOException>();
        await Assert.That(_pack.Count).IsEqualTo(0);
        await Assert.That(_seller.LaborPower).IsEqualTo((short)40);
        await Assert.That(_mail._allPlayerMails.Count).IsEqualTo(1);
        await Assert.That(Demand[(42, 2)].PendingSales).IsEqualTo(1);
    }

    [Test]
    public async Task Sale_TraderAboveRange_DoesNotConsumePackOrLabor()
    {
        _trader.Transform.Local.SetPosition(0, 0, 2.51f);
        await Assert.That(_specialty.SellSpecialty(_seller, 20)).IsEqualTo(0);
        await Assert.That(_pack.Count).IsEqualTo(1);
        await Assert.That(_seller.LaborPower).IsEqualTo((short)100);
        await Assert.That(_mail._allPlayerMails).IsEmpty();
        await Assert.That(Demand).IsEmpty();
        _trader.Transform.Local.SetPosition(0, 0, 2.5f);
        await Assert.That(_specialty.TryQuote(_seller, 20, out _, out _, out _, out _)).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Quote_UsesCurrentTraderZone_AndFallsBackWithoutAReachableTrader()
    {
        var now = DateTime.UtcNow;
        Demand[(42, 2)] = SpecialtyDemand.Create(42, 2, now, new SpecialtyConfig()) with { Ratio = 91 };
        Demand[(42, 3)] = SpecialtyDemand.Create(42, 3, now, new SpecialtyConfig()) with { Ratio = 102 };
        _seller.CurrentInteractionObject = _trader;
        await Assert.That(_specialty.GetRatioForSpecialty(_seller, 42)).IsEqualTo(91);
        _trader.Transform.Local.SetPosition(0, 0, 3);
        await Assert.That(_specialty.GetRatioForSpecialty(_seller, 42)).IsEqualTo(102);
        await Assert.That(_specialty.GetRatioForSpecialty(_seller, 43)).IsEqualTo(0);
    }

    [Test]
    public async Task Quote_KeepsBundlePrecedence_AndUsesMatrixForUnmappedTrader()
    {
        await Assert.That(_specialty.TryQuote(_seller, 20, out _, out _, out _, out var bundled)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(bundled).IsEqualTo(1100);
        _traders.Clear();
        await Assert.That(_specialty.TryQuote(_seller, 20, out _, out _, out _, out var matrix)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(matrix).IsEqualTo(3100);
    }

    [Test]
    public async Task Quote_SameOriginOrRemoteTrader_RejectsBeforePayment()
    {
        _pack.Template.SpecialtyZoneId = 2;
        await Assert.That(_specialty.TryQuote(_seller, 20, out _, out _, out _, out var bundlePrice)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(bundlePrice).IsEqualTo(1100);
        _traders.Clear();
        await Assert.That(_specialty.TryQuote(_seller, 20, out _, out _, out _, out _)).IsEqualTo(ErrorMessageType.StoreCantSellSameZone);
        _pack.Template.SpecialtyZoneId = 1;
        _trader.Transform.Local.SetPosition(10, 0, 0);
        await Assert.That(_specialty.TryQuote(_seller, 20, out _, out _, out _, out _)).IsEqualTo(ErrorMessageType.TooFarAway);
        await Assert.That(_mail._allPlayerMails).IsEmpty();
        await Assert.That(_pack.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Creation_InsufficientCertificates_RestoresDesignAndFirstCertificateStack()
    {
        var design = AddItem(_seller.Inventory.Bag, 99, 1);
        var certificate = AddItem(_seller.Inventory.Bag, Item.BoundTaxCertificate, 1);
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.HouseCreation);
            if (HousingManager.TryStageCreationPayment(mutation, _seller, design, 30000, true))
                throw new InvalidOperationException("The incomplete payment must fail.");
        }
        await Assert.That(design.Count).IsEqualTo(1);
        await Assert.That(certificate.Count).IsEqualTo(1);
        await Assert.That(_seller.Inventory.Bag.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Creation_ReservedMoney_RejectsAndRestoresDesign()
    {
        var design = AddItem(_seller.Inventory.Bag, 99, 1);
        using var reservation = new TradeReservation();
        await Assert.That(reservation.TryReserve(_seller, 900)).IsTrue();
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.HouseCreation);
            if (HousingManager.TryStageCreationPayment(mutation, _seller, design, 200, false))
                throw new InvalidOperationException("Reserved money cannot pay for a house.");
        }
        await Assert.That(design.Count).IsEqualTo(1);
        await Assert.That(_seller.Money).IsEqualTo(1000L);
    }

    [Test]
    public async Task Creation_FirstPayment_GivesOnePaidPeriodAndOneGracePeriod()
    {
        var house = new House();
        var now = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        HousingManager.SetInitialTaxDates(house, now);
        await Assert.That(house.PlaceDate).IsEqualTo(now);
        await Assert.That(house.TaxDueDate).IsEqualTo(now.AddDays(7));
        await Assert.That(house.ProtectionEndDate).IsEqualTo(now.AddDays(14));
    }

    private Dictionary<(uint, uint), SpecialtyDemand> Demand =>
        (Dictionary<(uint, uint), SpecialtyDemand>)typeof(SpecialtyManager).GetField("_demand", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_specialty)!;

    private Item AddItem(ItemContainer container, uint templateId, int count)
    {
        if (!_templates.TryGetValue(templateId, out var template))
            _templates[templateId] = template = new ItemTemplate { Id = templateId, MaxCount = 100 };
        var item = new Item { Id = _nextItem++, TemplateId = templateId, Template = template, Count = count,
            OwnerId = container.OwnerId, SlotType = container.ContainerType, Slot = container.Items.Count, _holdingContainer = container };
        container.Items.Add(item);
        container.UpdateFreeSlotCount();
        _allItems.Add(item.Id, item);
        return item;
    }

    private void ReplaceSingleton<T>(T instance) where T : class
    {
        var field = typeof(Singleton<>).MakeGenericType(typeof(T)).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons[typeof(T)] = field.GetValue(null);
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
