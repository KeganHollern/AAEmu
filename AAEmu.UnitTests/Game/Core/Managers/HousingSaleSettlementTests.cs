using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class HousingSaleSettlementTests
{
    private ItemManager _items;
    private MailManager _mail;
    private HousingManager _housing;
    private Mock<ISaveManager> _save;
    private Mock<IZoneManager> _zones;
    private Dictionary<ulong, Item> _allItems;
    private Dictionary<ulong, ItemContainer> _containers;
    private Dictionary<uint, ItemTemplate> _templates;
    private readonly Dictionary<Type, object> _singletons = [];
    private CharacterMock _seller, _buyer;
    private House _house;
    private NameManager _names;
    private uint _nextItem, _nextMail;
    private ulong _nextContainer;
    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 7 };
        _nextItem = 1000;
        _nextMail = 10000;
        _nextContainer = 1;
        var ids = Mock.Of<IItemIdManager>();
        ids.GetNextId().Returns(() => _nextItem++);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        ReplaceSingleton(_items);
        ReplaceSingleton(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        ReplaceSingleton(new HousingGameData());
        _allItems = [];
        _containers = [];
        _templates = [];
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_allPersistentContainers", _containers);
        SetField(_items, "_removedItems", new List<ulong>());
        SetField(_items, "_templates", _templates);
        _names = new NameManager();
        _names.Load([], [], []);
        ReplaceSingleton(_names);
        _seller = Character(1, "Seller");
        _buyer = Character(2, "Buyer");
        var mailIds = Mock.Of<IMailIdManager>();
        mailIds.GetNextId().Returns(() => _nextMail++);
        _mail = new MailManager(mailIds.Object, _names, _items, Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object, new Lazy<IHousingManager>(() => _housing),
            Mock.Of<ILocalizationManager>().Object) { _allPlayerMails = [] };
        SetField(_mail, "_deletedMailIds", new List<long>());
        _save = Mock.Of<ISaveManager>();
        _zones = Mock.Of<IZoneManager>();
        _housing = new HousingManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object, Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object, Mock.Of<IHousingTldManager>().Object,
            _items, _mail, _names, _zones.Object,
            Mock.Of<IDoodadManager>().Object, Mock.Of<IUccManager>().Object,
            new Lazy<ISaveManager>(() => _save.Object));
        _house = new House
        {
            Id = 42, TlId = 7, AccountId = _seller.AccountId, OwnerId = _seller.Id,
            CoOwnerId = _seller.Id, Name = "Test House", Faction = _seller.Faction,
            Template = new HousingTemplate { IsSellable = true, HousingBindingDoodad = [], Taxation = new Taxation { Tax = 5000 } },
            CurrentStep = -1, ProtectionEndDate = DateTime.UtcNow.AddDays(14), SellPrice = 100,
            IsDirty = false
        };
        SetField(_housing, "_houses", new Dictionary<uint, House> { [_house.Id] = _house });
        SetField(_housing, "_housesTl", new Dictionary<ushort, House> { [_house.TlId] = _house });
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        foreach (var (type, instance) in _singletons)
            typeof(Singleton<>).MakeGenericType(type).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, instance);
        _singletons.Clear();
    }

    [Test]
    public async Task Purchase_KnownSaveFailure_RestoresWalletListingOwnerAndOldTaxMail()
    {
        var oldBill = OldTaxMail();
        var original = HousingSaleState.Capture(_house);
        var observations = 0;
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(() =>
        {
            if (_house.OwnerId == _buyer.Id && _buyer.Money == 900 &&
                !_mail._allPlayerMails.ContainsKey(oldBill.Id) &&
                _mail._allPlayerMails.Values.Single(m => m.Header.SenderName == ".houseSold").Body.CopperCoins == 100)
                observations++;
            return false;
        });

        var result = _housing.BuyHouse(_house.TlId, 100, _buyer);

        await Assert.That(result).IsFalse();
        await Assert.That(observations).IsEqualTo(1);
        await Assert.That(HousingSaleState.Capture(_house)).IsEqualTo(original);
        await Assert.That(_buyer.Money).IsEqualTo(1000L);
        await Assert.That(_mail._allPlayerMails.Values).IsEquivalentTo([oldBill]);
    }

    [Test]
    public async Task Purchase_CommitsOfflineSellerProceedsAndPaidPeriod_ThenRejectsReplay()
    {
        OldTaxMail();
        var paidUntil = _house.ProtectionEndDate;
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);

        var bought = _housing.BuyHouse(_house.TlId, 100, _buyer);
        var replay = _housing.BuyHouse(_house.TlId, 100, _buyer);

        await Assert.That(bought).IsTrue();
        await Assert.That(replay).IsFalse();
        await Assert.That(_buyer.Money).IsEqualTo(900L);
        await Assert.That(_house.OwnerId).IsEqualTo(_buyer.Id);
        await Assert.That(_house.AccountId).IsEqualTo(_buyer.AccountId);
        await Assert.That(_house.SellPrice).IsEqualTo(0u);
        await Assert.That(_house.ProtectionEndDate).IsEqualTo(paidUntil);
        var proceeds = _mail._allPlayerMails.Values.Single(m => m.Header.SenderName == ".houseSold");
        await Assert.That(proceeds.Header.ReceiverId).IsEqualTo(_seller.Id);
        await Assert.That(proceeds.Body.CopperCoins).IsEqualTo(100);
        await Assert.That(_mail._allPlayerMails.Values.Any(MailForTax.IsTaxMail)).IsFalse();
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).WasCalled(Times.Once);
        var previousRecover = _house.AllowRecover;
        _housing.HousingToggleAllowRecover(_seller, _house.TlId);
        await Assert.That(_house.AllowRecover).IsEqualTo(previousRecover);
        var decoration = AddItem(_seller.Inventory.Bag, 200, 1);
        await Assert.That(_housing.DecorateHouse(_seller, _house.TlId, 0, default,
            System.Numerics.Quaternion.Identity, 0, decoration.Id)).IsFalse();
        await Assert.That(decoration._holdingContainer).IsSameReferenceAs(_seller.Inventory.Bag);
    }

    [Test]
    public async Task Purchase_TwoConcurrentBuyers_OnlyOnePaysAndReceivesProperty()
    {
        var other = Character(3, "Other");
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);
        var results = await Task.WhenAll(Task.Run(() => _housing.BuyHouse(_house.TlId, 100, _buyer)),
            Task.Run(() => _housing.BuyHouse(_house.TlId, 100, other)));
        await Assert.That(results.Count(r => r)).IsEqualTo(1);
        await Assert.That(_buyer.Money + other.Money).IsEqualTo(1900L);
        await Assert.That(_mail._allPlayerMails.Values.Count(m => m.Header.SenderName == ".houseSold")).IsEqualTo(1);
    }

    [Test]
    public async Task Purchase_RacingCancellation_SettlesEitherSaleOrRefundOnce()
    {
        Template(Item.AppraisalCertificate);
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);
        var results = await Task.WhenAll(Task.Run(() => _housing.BuyHouse(_house.TlId, 100, _buyer)),
            Task.Run(() => _housing.CancelForSale(_house, _seller)));
        await Assert.That(results.Count(r => r)).IsEqualTo(1);
        await Assert.That(_buyer.Money).IsEqualTo(results[0] ? 900L : 1000L);
        await Assert.That(_house.OwnerId).IsEqualTo(results[0] ? _buyer.Id : _seller.Id);
        await Assert.That(_seller.Inventory.MailAttachments.Items.Sum(i => i.Count)).IsEqualTo(results[0] ? 0 : 1);
        await Assert.That(_mail._allPlayerMails.Values.Count(m => m.Header.SenderName == ".houseSold"))
            .IsEqualTo(results[0] ? 1 : 0);
    }

    [Test]
    public async Task Purchase_DueTax_ReplacesOldOfferWithBuyerBillAndRetainsProtection()
    {
        var oldBill = OldTaxMail();
        _house.ProtectionEndDate = DateTime.UtcNow.AddHours(1);
        var protectedUntil = _house.ProtectionEndDate;
        _zones.GetZoneByKey(Any<uint>()).Returns(new Zone { GroupId = 9 });
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);

        await Assert.That(_housing.BuyHouse(_house.TlId, 100, _buyer)).IsTrue();

        var bill = _mail._allPlayerMails.Values.Single(MailForTax.IsTaxMail);
        await Assert.That(bill.Id).IsNotEqualTo(oldBill.Id);
        await Assert.That(bill.Header.ReceiverId).IsEqualTo(_buyer.Id);
        // The buyer inherits the overdue period and its approved one-time 10% fee.
        await Assert.That(bill.Body.BillingAmount).IsEqualTo(5500);
        await Assert.That((uint)bill.Header.Extra).IsEqualTo(_house.Id);
        await Assert.That(_house.ProtectionEndDate).IsEqualTo(protectedUntil);
    }

    [Test]
    public async Task Purchase_UncertainCommit_PreservesPreparedStateWithoutMailDelivery()
    {
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>())
            .Throws(new IOException("commit result unavailable"));
        var threw = false;
        try { _housing.BuyHouse(_house.TlId, 100, _buyer); }
        catch (IOException) { threw = true; }
        await Assert.That(threw).IsTrue();
        await Assert.That(_house.OwnerId).IsEqualTo(_buyer.Id);
        await Assert.That(_buyer.Money).IsEqualTo(900L);
        await Assert.That(_mail._allPlayerMails.Values.Count).IsEqualTo(2);
        await Assert.That(_mail._allPlayerMails.Values.Any(m => m.IsDelivered)).IsFalse();
    }

    [Test]
    public async Task Listing_KnownSaveFailure_RestoresExactCertificateAndDirtyState()
    {
        _house.SellPrice = 0;
        var certificate = AddItem(_seller.Inventory.Bag, Item.AppraisalCertificate, 2);
        certificate.IsDirty = false;
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(false);
        var listed = _housing.SetForSale(_house, 2_000_000, 0, _seller);
        await Assert.That(listed).IsFalse();
        await Assert.That(_house.SellPrice).IsEqualTo(0u);
        await Assert.That(_seller.Inventory.Bag.Items.Single()).IsSameReferenceAs(certificate);
        await Assert.That(certificate.Count).IsEqualTo(2);
        await Assert.That(certificate.IsDirty).IsFalse();
    }

    [Test]
    public async Task Listing_UsesUnreservedCertificatesAcrossStacks_AndPreservesTradeOffer()
    {
        _house.SellPrice = 0;
        var fullyReserved = AddItem(_seller.Inventory.Bag, Item.AppraisalCertificate, 2);
        var partlyReserved = AddItem(_seller.Inventory.Bag, Item.AppraisalCertificate, 2);
        var available = AddItem(_seller.Inventory.Bag, Item.AppraisalCertificate, 1);
        using var offer = new TradeReservation();
        await Assert.That(offer.TryReserve(fullyReserved, 2)).IsTrue();
        await Assert.That(offer.TryReserve(partlyReserved, 1)).IsTrue();
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);

        await Assert.That(_housing.SetForSale(_house, 2_000_000, 0, _seller)).IsTrue();

        await Assert.That(_house.SellPrice).IsEqualTo(2_000_000u);
        await Assert.That(fullyReserved.Count).IsEqualTo(2);
        await Assert.That(partlyReserved.Count).IsEqualTo(1);
        await Assert.That(available.Count).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedCount(fullyReserved)).IsEqualTo(2);
        await Assert.That(TradeReservation.GetReservedCount(partlyReserved)).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_KnownSaveFailure_RestoresListingAndDiscardsRefund()
    {
        Template(Item.AppraisalCertificate);
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(false);
        var cancelled = _housing.CancelForSale(_house, _seller);
        await Assert.That(cancelled).IsFalse();
        await Assert.That(_house.SellPrice).IsEqualTo(100u);
        await Assert.That(_seller.Inventory.MailAttachments.Items).IsEmpty();
        await Assert.That(_mail._allPlayerMails).IsEmpty();
    }

    [Test]
    public async Task Cancellation_CommitsOneRefund_ThenRejectsRepeatedCancellation()
    {
        Template(Item.AppraisalCertificate);
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);
        await Assert.That(_housing.CancelForSale(_house, _seller)).IsTrue();
        await Assert.That(_housing.CancelForSale(_house, _seller)).IsFalse();
        await Assert.That(_seller.Inventory.MailAttachments.Items.Sum(i => i.Count)).IsEqualTo(1);
        await Assert.That(_mail._allPlayerMails.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_MaximumPrice_SplitsCertificateRefundIntoLegalMails()
    {
        _house.SellPrice = int.MaxValue;
        Template(Item.AppraisalCertificate);
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);
        await Assert.That(_housing.CancelForSale(_house, _seller)).IsTrue();
        await Assert.That(_seller.Inventory.MailAttachments.Items.Sum(i => i.Count)).IsEqualTo(2148);
        await Assert.That(_mail._allPlayerMails.Count).IsEqualTo(3);
        await Assert.That(_mail._allPlayerMails.Values.All(m => m.Body.Attachments.Count <= 10)).IsTrue();
    }

    [Test]
    public async Task Furniture_FailedSale_RestoresGuestBoundItemAndCofferContentsAndOwnership()
    {
        var guest = Character(3, "Guest");
        var bound = AddItem(guest.Inventory.SystemContainer, 200, 1);
        bound.ItemFlags |= ItemFlag.SoulBound;
        var coffer = new DoodadCoffer { ObjId = 90, DbId = 900, OwnerId = guest.Id, OwnerDbId = _house.Id,
            OwnerType = DoodadOwnerType.Housing, ItemId = bound.Id, TemplateId = 500, IsPersistent = true,
            OpenedBy = guest, AttachPoint = AttachPointKind.None, Capacity = 10, ItemContainer = new CofferContainer(guest.Id, false) { ContainerId = _nextContainer++, Owner = guest, ContainerSize = 10 } };
        _containers.Add(coffer.ItemContainer.ContainerId, coffer.ItemContainer);
        var content = AddItem(coffer.ItemContainer, 201, 4);
        var data = HousingGameData.Instance;
        SetField(data, "_housingDecorations", new Dictionary<uint, HousingDecoration> { [10] = new() { Id = 10, DoodadId = 500 } });
        SetField(data, "_housingItemHousingDecorations", new List<ItemHousingDecoration> { new() { DesignId = 10, ItemId = 200 } });
        _house.AttachedDoodads.Add(coffer);
        coffer.Transform.Parent = _house.Transform;
        var preparedForGuest = false;
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(() =>
        {
            preparedForGuest = coffer.ItemContainer.Items.Count == 0 && bound.SlotType == SlotType.Mail &&
                content.SlotType == SlotType.Mail && _mail._allPlayerMails.Values.Single(m =>
                    m.Header.SenderName == ".houseDemolish").Header.ReceiverId == guest.Id;
            return false;
        });

        var result = _housing.BuyHouse(_house.TlId, 100, _buyer);

        await Assert.That(result).IsFalse();
        await Assert.That(preparedForGuest).IsTrue();
        await Assert.That(bound._holdingContainer).IsSameReferenceAs(guest.Inventory.SystemContainer);
        await Assert.That(content._holdingContainer).IsSameReferenceAs(coffer.ItemContainer);
        await Assert.That(coffer.ItemContainer.OwnerId).IsEqualTo(guest.Id);
        await Assert.That(coffer.OpenedBy).IsSameReferenceAs(guest);
        await Assert.That(coffer.IsPersistent).IsTrue();
        await Assert.That(coffer.ItemId).IsEqualTo(bound.Id);
        await Assert.That(guest.Inventory.MailAttachments.Items).IsEmpty();
        await Assert.That(_mail._allPlayerMails).IsEmpty();
    }

    [Test]
    public async Task Furniture_UnboundCoffer_TransfersExactBackingItemAndReturnsContentsToGuest()
    {
        var guest = Character(3, "Guest");
        var backing = AddItem(guest.Inventory.SystemContainer, 200, 1);
        backing.UccId = 9876;
        var coffer = new DoodadCoffer { ObjId = 90, DbId = 900, OwnerId = guest.Id, OwnerDbId = _house.Id,
            OwnerType = DoodadOwnerType.Housing, ItemId = backing.Id, TemplateId = 500, IsPersistent = true,
            OpenedBy = guest, AttachPoint = AttachPointKind.None, Capacity = 10,
            ItemContainer = new CofferContainer(guest.Id, false) { ContainerId = _nextContainer++, Owner = guest, ContainerSize = 10 } };
        _containers.Add(coffer.ItemContainer.ContainerId, coffer.ItemContainer);
        var content = AddItem(coffer.ItemContainer, 201, 4);
        SetField(HousingGameData.Instance, "_housingDecorations", new Dictionary<uint, HousingDecoration>
            { [10] = new() { Id = 10, DoodadId = 500 } });
        SetField(HousingGameData.Instance, "_housingItemHousingDecorations", new List<ItemHousingDecoration>
            { new() { DesignId = 10, ItemId = 200 } });
        _house.AttachedDoodads.Add(coffer);
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);

        await Assert.That(_housing.BuyHouse(_house.TlId, 100, _buyer)).IsTrue();

        await Assert.That(backing._holdingContainer).IsSameReferenceAs(_buyer.Inventory.SystemContainer);
        await Assert.That(backing.OwnerId).IsEqualTo((ulong)_buyer.Id);
        await Assert.That(backing.UccId).IsEqualTo(9876UL);
        await Assert.That(coffer.OwnerId).IsEqualTo(_buyer.Id);
        await Assert.That(coffer.ItemContainer.OwnerId).IsEqualTo(_buyer.Id);
        await Assert.That(coffer.ItemContainer.Items).IsEmpty();
        await Assert.That(coffer.OpenedBy).IsNull();
        await Assert.That(coffer.IsPersistent).IsTrue();
        await Assert.That(content._holdingContainer).IsSameReferenceAs(guest.Inventory.MailAttachments);
        await Assert.That(_mail._allPlayerMails.Values.Single(m => m.Header.SenderName == ".houseDemolish")
            .Body.Attachments.Single()).IsSameReferenceAs(content);

        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => _zones.Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        ReplaceSingleton(worldManager);
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [1] = world });
        guest.ParentWorld = _buyer.ParentWorld = coffer.ParentWorld = world;
        ((ConcurrentDictionary<uint, Doodad>)typeof(WorldInstance)
            .GetField("_doodads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!)
            .TryAdd(coffer.ObjId, coffer);
        coffer.SetData((int)HousingPermission.Public);
        var guestItem = AddItem(guest.Inventory.Bag, 202, 1);
        await Assert.That(guest.Inventory.SwapCofferItems(guestItem.Id, 0, SlotType.Inventory,
            (byte)guestItem.Slot, SlotType.Trade, 0, coffer.ItemContainer.ContainerId)).IsFalse();
        var doodads = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            _items, new Lazy<IHousingManager>(() => _housing), Mock.Of<ISusManager>().Object);
        await Assert.That(doodads.OpenCofferDoodad(_buyer, coffer.ObjId)).IsTrue();
        var buyerItem = AddItem(_buyer.Inventory.Bag, 202, 1);
        await Assert.That(_buyer.Inventory.SwapCofferItems(buyerItem.Id, 0, SlotType.Inventory,
            (byte)buyerItem.Slot, SlotType.Trade, 0, coffer.ItemContainer.ContainerId)).IsTrue();
    }

    [Test]
    public async Task Furniture_PostCommitNotificationFailure_PreservesReturnedItemsAndPreventsRecovery()
    {
        var guest = Character(3, "Guest");
        var backing = AddItem(guest.Inventory.SystemContainer, 200, 1);
        backing.ItemFlags |= ItemFlag.SoulBound;
        var coffer = new DoodadCoffer { ObjId = 90, DbId = 900, OwnerId = guest.Id, OwnerDbId = _house.Id,
            OwnerType = DoodadOwnerType.Housing, ItemId = backing.Id, ItemTemplateId = 200,
            TemplateId = 500, IsPersistent = true, OpenedBy = guest, AttachPoint = AttachPointKind.None,
            Capacity = 10, ItemContainer = new CofferContainer(guest.Id, false)
                { ContainerId = _nextContainer++, Owner = guest, ContainerSize = 10 } };
        _containers.Add(coffer.ItemContainer.ContainerId, coffer.ItemContainer);
        var content = AddItem(coffer.ItemContainer, 201, 4);
        SetField(HousingGameData.Instance, "_housingDecorations", new Dictionary<uint, HousingDecoration>
            { [10] = new() { Id = 10, DoodadId = 500 } });
        SetField(HousingGameData.Instance, "_housingItemHousingDecorations", new List<ItemHousingDecoration>
            { new() { DesignId = 10, ItemId = 200 } });
        _house.AttachedDoodads.Add(coffer);
        _buyer.Connection = new GameConnection(new ThrowingSession());
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(true);

        var threw = false;
        try { _housing.BuyHouse(_house.TlId, 100, _buyer); }
        catch (IOException) { threw = true; }
        _buyer.Connection = null;

        await Assert.That(threw).IsTrue();
        await Assert.That(_house.OwnerId).IsEqualTo(_buyer.Id);
        await Assert.That(_buyer.Money).IsEqualTo(900L);
        await Assert.That(backing._holdingContainer).IsSameReferenceAs(guest.Inventory.MailAttachments);
        await Assert.That(content._holdingContainer).IsSameReferenceAs(guest.Inventory.MailAttachments);
        await Assert.That(coffer.IsPersistent).IsFalse();
        await Assert.That(coffer.Despawn > DateTime.MinValue).IsTrue();
        await Assert.That(coffer.ItemId).IsEqualTo(0UL);
        await Assert.That(coffer.OpenedBy).IsNull();
        await Assert.That(coffer.ItemContainer.Items).IsEmpty();

        var doodads = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            _items, new Lazy<IHousingManager>(() => _housing), Mock.Of<ISusManager>().Object);
        ReplaceSingleton(doodads);
        SetField(doodads, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
            { [nameof(DoodadFuncRecoverItem)] = new() { [1] = new DoodadFuncRecoverItem() } });
        await Assert.That(coffer.DoFunc(guest, 0,
            new DoodadFunc { FuncId = 1, FuncType = nameof(DoodadFuncRecoverItem) })).IsTrue();
        await Assert.That(guest.Inventory.Bag.Items).IsEmpty();
        await Assert.That(guest.Inventory.MailAttachments.Items.Count).IsEqualTo(2);
        await Assert.That(_mail._allPlayerMails.Values.Single(m => m.Header.SenderName == ".houseDemolish")
            .Body.Attachments.Count).IsEqualTo(2);
        await Assert.That(_housing.BuyHouse(_house.TlId, 100, _buyer)).IsFalse();
        await Assert.That(_buyer.Money).IsEqualTo(900L);
    }

    [Test]
    public async Task Furniture_ReturnedParent_ReparentsChildWithoutMovingIt_AndRollbackRestoresLinks()
    {
        var backing = AddItem(_seller.Inventory.SystemContainer, 200, 1);
        backing.ItemFlags |= ItemFlag.SoulBound;
        var parent = new Doodad { ObjId = 90, DbId = 900, OwnerId = _seller.Id, OwnerDbId = _house.Id,
            OwnerType = DoodadOwnerType.Housing, ItemId = backing.Id, TemplateId = 500,
            AttachPoint = AttachPointKind.None, IsPersistent = true };
        var child = new Doodad { ObjId = 91, DbId = 901, OwnerId = _seller.Id, OwnerDbId = _house.Id,
            OwnerType = DoodadOwnerType.Housing, TemplateId = 500, ItemTemplateId = 200,
            AttachPoint = AttachPointKind.None, IsPersistent = true };
        parent.Transform.Local.Position = new(3, 4, 5);
        parent.Transform.Parent = _house.Transform;
        child.Transform.Parent = parent.Transform;
        child.Transform.Local.Position = new(1, 2, 3);
        child.ParentObjId = parent.ObjId;
        var oldLocal = child.Transform.Local.Position;
        var oldWorld = child.Transform.World.Position;
        SetField(HousingGameData.Instance, "_housingDecorations", new Dictionary<uint, HousingDecoration>
            { [10] = new() { Id = 10, DoodadId = 500 } });
        SetField(HousingGameData.Instance, "_housingItemHousingDecorations", new List<ItemHousingDecoration>
            { new() { DesignId = 10, ItemId = 200 } });
        _house.AttachedDoodads.AddRange([parent, child]);
        var keptWorldPose = false;
        _save.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>()).Returns(() =>
        {
            keptWorldPose = child.Transform.Parent == _house.Transform && child.Transform.World.Position == oldWorld;
            return false;
        });

        await Assert.That(_housing.BuyHouse(_house.TlId, 100, _buyer)).IsFalse();

        await Assert.That(keptWorldPose).IsTrue();
        await Assert.That(child.Transform.Parent).IsSameReferenceAs(parent.Transform);
        await Assert.That(child.Transform.Local.Position).IsEqualTo(oldLocal);
        await Assert.That(child.ParentObjId).IsEqualTo(parent.ObjId);
        await Assert.That(parent.IsPersistent).IsTrue();
    }

    private CharacterMock Character(uint id, string name)
    {
        var character = new CharacterMock { Id = id, Name = name, AccountId = id * 10, Money = 1000,
            NumInventorySlots = 10, NumBankSlots = 10, Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance } };
        _names.AddCharacter(id, name, character.AccountId);
        foreach (var type in Enum.GetValues<SlotType>().Where(t => t != SlotType.EquipmentMate))
        {
            var container = new ItemContainer(id, type, false, character) { Owner = character, ContainerId = _nextContainer++ };
            _containers.Add(container.ContainerId, container);
        }
        character.Inventory = new Inventory(character);
        character.Mails = new CharacterMails(character);
        return character;
    }

    private ItemTemplate Template(uint id)
    {
        if (!_templates.TryGetValue(id, out var template))
            _templates[id] = template = new ItemTemplate { Id = id, MaxCount = 100, BindType = ItemBindType.Normal };
        return template;
    }

    private Item AddItem(ItemContainer container, uint template, int count)
    {
        var item = new Item { Id = _nextItem++, TemplateId = template, Template = Template(template), Count = count,
            OwnerId = container.OwnerId, SlotType = container.ContainerType, Slot = container.Items.Count, _holdingContainer = container };
        _allItems.Add(item.Id, item);
        container.Items.Add(item);
        container.UpdateFreeSlotCount();
        return item;
    }

    private BaseMail OldTaxMail()
    {
        var mail = new BaseMail { Id = 5, MailType = MailType.Billing, ReceiverName = _seller.Name,
            Header = { ReceiverId = _seller.Id, SenderId = 0, SenderName = ".houseTax", Extra = _house.Id } };
        _mail._allPlayerMails[mail.Id] = mail;
        return mail;
    }

    private void ReplaceSingleton<T>(T instance) where T : class
    {
        var field = typeof(Singleton<>).MakeGenericType(typeof(T)).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons[typeof(T)] = field.GetValue(null);
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ThrowingSession : ISession
    {
        public System.Net.IPAddress Ip => System.Net.IPAddress.Loopback;
        public uint SessionId => 1;
        public System.Net.Sockets.Socket Socket => null;
        public void SendPacket(byte[] packet) => throw new IOException("Sale notification failed after commit");
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
