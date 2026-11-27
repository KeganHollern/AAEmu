using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class CSBuyItemsPacketTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private CharacterMock _character;
    private Doodad _shop;
    private DoodadFunc _function;
    private DoodadFuncStoreUi _store;
    private Dictionary<uint, MerchantGoods> _packs;
    private RecordingSession _session;
    private WorldManager _worlds;

    [Before(Test)]
    public void SetUp()
    {
        var itemIds = Mock.Of<IItemIdManager>();
        uint itemId = 100;
        itemIds.GetNextId().Returns(() => itemId++);
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, itemIds.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        Install(items);
        Install(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        var names = new NameManager();
        SetField(names, "_characterAccounts", new Dictionary<uint, uint> { [7] = 70, [8] = 70, [9] = 90 });
        Install(names);
        SetField(items, "_allItems", new Dictionary<ulong, Item>());
        SetField(items, "_removedItems", new List<ulong>());
        SetField(items, "_templates", new Dictionary<uint, ItemTemplate>
        {
            [23633] = new() { Id = 23633, MaxCount = 100 },
            [21166] = new() { Id = 21166, MaxCount = 1, Price = 100000 },
            [23681] = new() { Id = 23681, MaxCount = 1, Price = 1000 },
            [23682] = new() { Id = 23682, MaxCount = 1, Price = 1000 },
            [16197] = new() { Id = 16197, MaxCount = 1, Price = 5000 },
            [1000] = new() { Id = 1000, MaxCount = 100, FixedGrade = -1, Price = 10, HonorPrice = 20, LivingPointPrice = 30 },
            [1001] = new() { Id = 1001, MaxCount = 100, FixedGrade = -1, Price = 1 }
        });
        _packs = [];
        foreach (var (id, kind) in new (uint, byte)[] { (145, 0), (164, 3), (192, 1), (999, 0) })
        {
            var pack = new MerchantGoods(id, kind);
            pack.AddItemToStock(id == 999 ? 1001U : 1000U, 2);
            _packs.Add(id, pack);
        }
        var npcs = new NpcManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IModelManager>().Object,
            Mock.Of<IFactionManager>().Object, items, Mock.Of<IAIManager>().Object);
        SetField(npcs, "<Goods>k__BackingField", _packs);
        Install(npcs);
        var doodads = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            items, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        _store = new DoodadFuncStoreUi { Id = 14, MerchantPackId = 145 };
        SetField(doodads, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncPurchase)] = new()
            {
                [382] = new DoodadFuncPurchase { Id = 382, ItemId = 23681, Count = 1 },
                [383] = new DoodadFuncPurchase { Id = 383, ItemId = 23682, Count = 1 },
                [25] = new DoodadFuncPurchase { Id = 25, ItemId = 16197, Count = 1 },
                [134] = new DoodadFuncPurchase { Id = 134, ItemId = 21166, Count = 1, CoinItemId = 23633, CoinCount = 15 }
            },
            [nameof(DoodadFuncStoreUi)] = new() { [14] = _store }
        });
        Install(doodads);
        _worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(_worlds);
        var world = CreateWorld(1);
        _session = new RecordingSession();
        _character = new CharacterMock
        {
            Id = 7,
            AccountId = 70,
            ObjId = 7,
            Name = "Buyer",
            Money = 1000,
            HonorPoint = 1000,
            VocationPoint = 1000,
            NumInventorySlots = 5,
            NumBankSlots = 5,
            ParentWorld = world,
            Connection = new GameConnection(_session)
        };
        _character.Connection.ActiveChar = _character;
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_character.Id, type, false, _character)
            { Owner = _character, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _character.Inventory = new Inventory(_character);
        _character.BuyBackItems = new ItemContainer(_character.Id, SlotType.None, false, _character) { Owner = _character };
        _shop = new Doodad { ObjId = 99, TemplateId = 6090, ParentWorld = world, OwnerId = 7 };
        _function = new DoodadFunc { FuncId = 14, FuncType = nameof(DoodadFuncStoreUi), SkillId = 12087, NextPhase = -1 };
        _shop.CurrentFuncs.Add(_function);
        world.AddObject(_shop);
        // r208022 opens StoreUi without a server skill packet or interaction session.
        _character.CurrentInteractionObject = null;
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, instance) in _previousInstances)
            field.SetValue(null, instance);
        _previousInstances.Clear();
    }

    [Test]
    [Arguments(145U, ShopCurrencyType.Money, 20, 0, 0)]
    [Arguments(164U, ShopCurrencyType.VocationBadges, 0, 0, 60)]
    [Arguments(192U, ShopCurrencyType.Honor, 0, 40, 0)]
    public async Task ClientLocalShop_ZeroCatalogFieldBuysFromAuthoredPack(
        uint pack, ShopCurrencyType currency, int money, int honor, int vocation)
    {
        _store.MerchantPackId = pack;
        _shop.Transform.Local.SetPosition(3, 0, 0);
        var stream = SendPurchase(currency: currency);

        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.CurrentInteractionObject).IsNull();
        await Assert.That(_character.Money).IsEqualTo(1000L - money);
        await Assert.That(_character.HonorPoint).IsEqualTo(1000 - honor);
        await Assert.That(_character.VocationPoint).IsEqualTo(1000 - vocation);
        var item = _character.Inventory.Bag.Items.Single();
        await Assert.That(item.TemplateId).IsEqualTo(1000U);
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(item.Grade).IsEqualTo((byte)2);
        await Assert.That(_session.Packets.Any(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCItemTaskSuccessPacket)).IsTrue();
    }

    private void OpenFoalStall(uint template)
    {
        _shop.TemplateId = template;
        (_function.GroupId, _function.FuncKey, _function.FuncId, _function.SkillId) = template switch
        {
            5943 => (15312U, 12667U, 382U, 20218U), // Gray Lilyut Foal.
            5944 => (15313U, 12668U, 383U, 20219U), // Buckskin Lilyut Foal.
            _ => throw new ArgumentOutOfRangeException(nameof(template))
        };
        _function.FuncType = nameof(DoodadFuncPurchase);
    }

    private void BuyFoal(uint item) => SendPurchase(clientShopId: _function.FuncKey,
        requests: [new StorePurchaseRequest(item, 1, ShopCurrencyType.Money)]);

    [Test]
    [Arguments(2f)]
    [Arguments(4f)]
    public async Task FoalStalls_ChargeEachDisplayedPriceAndGrantTheChosenAnimal(float distance)
    {
        _character.Money = 2000;
        _shop.Transform.Local.SetPosition(distance, 0, 0);
        OpenFoalStall(5943);
        BuyFoal(23681);
        await Assert.That(_character.Money).IsEqualTo(1000L);
        await Assert.That(_character.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(23681U);

        OpenFoalStall(5944);
        BuyFoal(23682);
        await Assert.That(_character.Money).IsZero();
        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(2);
        await Assert.That(_character.Inventory.Bag.Items.Count(item => item.TemplateId == 23682 && item.Count == 1)).IsEqualTo(1);
    }

    [Test]
    [Arguments(999, 2f, 23682U, ErrorMessageType.NotEnoughMoney)]
    [Arguments(1000, 4.1f, 23682U, ErrorMessageType.TooFarAway)]
    [Arguments(1000, 2f, 23681U, ErrorMessageType.StoreInvalidItem)]
    public async Task FoalStalls_KeepTheWalletAndBagWhenAQuoteCannotSettle(
        int money, float distance, uint item, ErrorMessageType expected)
    {
        OpenFoalStall(5944);
        _character.Money = money;
        _shop.Transform.Local.SetPosition(distance, 0, 0);
        BuyFoal(item);
        await Assert.That(_character.Money).IsEqualTo((long)money);
        await Assert.That(_character.Inventory.Bag.Items).IsEmpty();
        await AssertSingleError(expected);
    }

    [Test]
    public async Task FoalStalls_KeepTheSeparateLimitedPurchaseSkillOutsideTheGoldDisplay()
    {
        OpenFoalStall(5944);
        _function.SkillId = 23942; // Limited Purchase per Account has its own contract.
        BuyFoal(23682);
        await AssertUnchanged(ErrorMessageType.StoreHaveProblem);
    }

    private void OpenBlueStatueDisplay()
    {
        _shop.TemplateId = 3206;
        _function.FuncKey = 6542;
        _function.FuncId = 25;
        _function.GroupId = 7312;
        _function.SkillId = 14569;
        _function.FuncType = nameof(DoodadFuncPurchase);
        _character.Money = 40000;
    }

    private void BuyBlueStatue() => SendPurchase(clientShopId: 6542,
        requests: [new StorePurchaseRequest(16197, 1, ShopCurrencyType.Money)]);

    [Test]
    [Arguments(1.9f)]
    [Arguments(4f)]
    public async Task BlueStatueConfirmation_DebitsTheDisplayedPriceAndGrantsOneItem(float distance)
    {
        OpenBlueStatueDisplay();
        _shop.Transform.Local.SetPosition(distance, 0, 0);
        BuyBlueStatue();

        await Assert.That(_character.Money).IsEqualTo(35000L);
        var item = _character.Inventory.Bag.Items.Single();
        await Assert.That(item.TemplateId).IsEqualTo(16197U);
        await Assert.That(item.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BlueStatueConfirmation_GrantsItsStockAchievementOnAcquisition()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_MERCHANT_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_MERCHANT_COMPACT for the stock statue achievement.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var data = new AchievementGameData();
        data.Load(connection);
        data.PostLoad();
        var requirements = new UnitRequirementsGameData();
        requirements.Load(connection);
        var achievements = new CharacterAchievements(_character, data, TimeProvider.System,
            persistStateOverride: () => true, unitRequirementsData: requirements);
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_character, achievements);
        OpenBlueStatueDisplay();

        BuyBlueStatue();

        await Assert.That(achievements.IsCompleted(203)).IsTrue();
        await Assert.That(achievements.GetAmount(203)).IsEqualTo(1U);
        await Assert.That(achievements.IsCompleted(204)).IsFalse();
    }

    [Test]
    [Arguments(4999, 5, 2f, ErrorMessageType.NotEnoughMoney)]
    [Arguments(40000, 0, 2f, ErrorMessageType.BagFull)]
    [Arguments(40000, 5, 4.1f, ErrorMessageType.TooFarAway)]
    public async Task BlueStatueConfirmation_PreservesMoneyAndBagOnRefusal(
        int money, int bagSize, float distance, ErrorMessageType expected)
    {
        OpenBlueStatueDisplay();
        _character.Money = money;
        _character.Inventory.Bag.ContainerSize = bagSize;
        _shop.Transform.Local.SetPosition(distance, 0, 0);
        BuyBlueStatue();

        await Assert.That(_character.Money).IsEqualTo((long)money);
        await Assert.That(_character.Inventory.Bag.Items).IsEmpty();
        await AssertSingleError(expected);
    }

    [Test]
    public async Task BlueStatueConfirmation_ClosedOfferPreservesTheWalletAndBag()
    {
        OpenBlueStatueDisplay();
        _shop.CurrentFuncs.Clear();
        BuyBlueStatue();

        await Assert.That(_character.Money).IsEqualTo(40000L);
        await Assert.That(_character.Inventory.Bag.Items).IsEmpty();
        await AssertSingleError(ErrorMessageType.StoreHaveProblem);
    }

    [Test]
    [Arguments(16198U, 1)]
    [Arguments(16197U, 2)]
    public async Task BlueStatueConfirmation_RejectsAQuoteWhenTheActiveItemOrAmountChanged(uint itemId, int count)
    {
        OpenBlueStatueDisplay();
        var offer = (DoodadFuncPurchase)DoodadManager.Instance.GetFuncTemplate(_function.FuncId, _function.FuncType);
        offer.ItemId = itemId;
        offer.Count = count;

        BuyBlueStatue();

        await Assert.That(_character.Money).IsEqualTo(40000L);
        await Assert.That(_character.Inventory.Bag.Items).IsEmpty();
        await AssertSingleError(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    public async Task CottageDesignDisplay_TakesFifteenGildaStarsThroughItsTokenConfirmation()
    {
        _shop.TemplateId = 4729;
        _function.FuncId = 134;
        _function.FuncKey = 10262;
        _function.SkillId = 14569;
        _function.GroupId = 11691;
        _function.FuncType = nameof(DoodadFuncPurchase);
        var byId = new Dictionary<uint, DoodadFunc> { [10262] = _function };
        SetField(DoodadManager.Instance, "_funcsById", byId);
        var bag = _character.Inventory.Bag;
        await Assert.That(bag.AcquireDefaultItem(ItemTaskType.DoodadInteraction, 23633, 15)).IsTrue();

        var confirmation = new PacketStream().WriteBc(_shop.ObjId).Write(_function.FuncKey);
        new CSBuyCoinItemPacket { Connection = _character.Connection }.Read(new PacketStream(confirmation.GetBytes()));

        await Assert.That(_character.Money).IsEqualTo(1000L);
        var item = bag.Items.Single();
        await Assert.That(item.TemplateId).IsEqualTo(21166U);
        await Assert.That(item.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(7U)]
    [Arguments(8U)]
    public async Task WorkstationSameAccount_AllowsOwnerAndAnotherCharacterOnTheAccount(uint owner)
    {
        _store.MerchantPackId = 164;
        _function.PermId = (uint)DoodadFuncPermission.SameAccount;
        _shop.OwnerId = owner;
        SendPurchase(currency: ShopCurrencyType.VocationBadges);

        await Assert.That(_character.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(_character.VocationPoint).IsEqualTo(940);
    }

    [Test]
    public async Task WorkstationAnotherAccount_RejectsPurchase()
    {
        _function.PermId = (uint)DoodadFuncPermission.SameAccount;
        _shop.OwnerId = 9;
        SendPurchase();

        await AssertUnchanged(ErrorMessageType.InteractionPermissionDeny);
    }

    [Test]
    [Arguments("noShop")]
    [Arguments("previousPhase")]
    [Arguments("missingFunctionTemplate")]
    [Arguments("missingPack")]
    public async Task Purchase_RequiresTheCurrentStoreFunctionAndLoadedPack(string invalid)
    {
        switch (invalid)
        {
            case "noShop": _function.FuncType = nameof(DoodadFuncBankUi); break;
            case "previousPhase": _shop.CurrentFuncs.Clear(); break;
            case "missingFunctionTemplate": _function.FuncId = 999; break;
            case "missingPack": _store.MerchantPackId = 998; break;
        }
        SendPurchase(clientShopId: 145);

        await AssertUnchanged(ErrorMessageType.StoreHaveProblem);
    }

    [Test]
    public async Task ForgedCatalog_CannotBuyAnItemOutsideTheAuthoredPack()
    {
        SendPurchase(clientShopId: 999, itemId: 1001);

        await AssertUnchanged(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    [Arguments(0U)]
    [Arguments(145U)]
    [Arguments(999U)]
    public async Task OpaqueClientTypeField_DoesNotSelectTheCatalog(uint clientType)
    {
        SendPurchase(clientShopId: clientType);

        await Assert.That(_character.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(1000U);
        await Assert.That(_character.Money).IsEqualTo(980L);
    }

    [Test]
    public async Task NpcPurchase_KeepsTheNpcAuthoredCatalog()
    {
        var npc = new Npc
        {
            ObjId = 77,
            ParentWorld = _character.ParentWorld,
            Template = new NpcTemplate { Merchant = true, MerchantPackIds = [145] }
        };
        _character.ParentWorld.SetNpc(npc.ObjId, npc);
        SendPurchase(clientShopId: 999, npcId: 77, doodadId: 0);

        await Assert.That(_character.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(1000U);
        await Assert.That(_character.Money).IsEqualTo(980L);
    }

    [Test]
    public async Task NpcPurchase_BuysItemsFromBothAuthoredPacksInOneTransaction()
    {
        AddNpc([145, 999]);
        var stream = SendPurchase(npcId: 77, doodadId: 0,
            requests: [new(1000, 2, ShopCurrencyType.Money), new(1001, 2, ShopCurrencyType.Money)]);

        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.Money).IsEqualTo(978L);
        await Assert.That(_character.Inventory.Bag.Items.Select(item => item.TemplateId))
            .IsEquivalentTo(new uint[] { 1000, 1001 });
        await Assert.That(_character.Inventory.Bag.Items.All(item => item.Count == 2 && item.Grade == 2)).IsTrue();
    }

    [Test]
    public async Task NpcPurchase_EmptyLastPackDoesNotHideEarlierGoods()
    {
        _packs[998] = new MerchantGoods(998);
        AddNpc([145, 998]);
        SendPurchase(npcId: 77, doodadId: 0);

        await Assert.That(_character.Money).IsEqualTo(980L);
        await Assert.That(_character.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(1000U);
    }

    [Test]
    public async Task NpcPurchase_CannotBuyFromAnUnrelatedPack()
    {
        _packs[998] = new MerchantGoods(998);
        AddNpc([145, 998]);
        SendPurchase(npcId: 77, doodadId: 0, itemId: 1001, clientShopId: 999);

        await AssertUnchanged(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    public async Task NpcPurchase_MeasuresThreeMetresFromScaledActorEdges()
    {
        using var models = new QuestInteractionTestModels();
        var npc = AddNpc([145]);
        npc.ModelId = _character.ModelId = 1;
        npc.Template.Scale = 6f;
        // Align the sphere centers vertically. The two radii sum to 3.5 m.
        npc.Transform.Local.SetPosition(6.51f, 0, -5);
        SendPurchase(npcId: npc.ObjId, doodadId: 0);
        await AssertUnchanged(ErrorMessageType.TooFarAway);

        _session.Packets.Clear();
        npc.Transform.Local.SetPosition(6.5f, 0, -5);
        SendPurchase(npcId: npc.ObjId, doodadId: 0);
        await Assert.That(_character.Inventory.Bag.Items.Single().Count).IsEqualTo(2);
        await Assert.That(_character.Money).IsEqualTo(980L);
    }

    [Test]
    public async Task NpcPurchase_DistantMultiPackMerchantDoesNotGrantItems()
    {
        var npc = AddNpc([145, 999]);
        npc.Transform.Local.SetPosition(3.01f, 0, 0);
        SendPurchase(npcId: 77, doodadId: 0);

        await AssertUnchanged(ErrorMessageType.TooFarAway);
    }

    [Test]
    public async Task RemoteHonorPurchase_KeepsItsCurrencyCatalog()
    {
        SendPurchase(clientShopId: 999, currency: ShopCurrencyType.Honor, doodadId: 0);

        await Assert.That(_character.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(1000U);
        await Assert.That(_character.HonorPoint).IsEqualTo(960);
        await Assert.That(_character.Money).IsEqualTo(1000L);
    }

    [Test]
    public async Task ForgedCurrency_CannotChangeTheAuthoredPackPrice()
    {
        SendPurchase(currency: ShopCurrencyType.Honor);

        await AssertUnchanged(ErrorMessageType.StoreInvalidItem);
    }

    [Test]
    [Arguments("horizontal")]
    [Arguments("vertical")]
    [Arguments("nan")]
    [Arguments("missing")]
    [Arguments("despawn")]
    [Arguments("otherInstance")]
    [Arguments("staleObjectId")]
    public async Task Purchase_RequiresALiveShopWithinThreeMetres(string invalid)
    {
        switch (invalid)
        {
            case "horizontal": _shop.Transform.Local.SetPosition(3.01f, 0, 0); break;
            case "vertical": _shop.Transform.Local.SetPosition(0, 0, 3.01f); break;
            case "nan": _shop.Transform.Local.SetPosition(float.NaN, 0, 0); break;
            case "missing": _shop.ParentWorld.RemoveObject(_shop); break;
            case "despawn": _shop.Despawn = DateTime.UtcNow.AddSeconds(1); break;
            case "otherInstance": _shop.ParentWorld = CreateWorld(2); break;
            case "staleObjectId": _shop.ObjId++; break;
        }
        SendPurchase();

        await AssertUnchanged(ErrorMessageType.TooFarAway);
    }

    [Test]
    public async Task PermissionChangedAfterOnePurchase_RejectsTheNextPurchase()
    {
        SendPurchase();
        _function.PermId = (uint)DoodadFuncPermission.OwnerOnly;
        _shop.OwnerId = 9;
        _session.Packets.Clear();
        SendPurchase();

        await Assert.That(_character.Money).IsEqualTo(980L);
        await Assert.That(_character.Inventory.Bag.Items.Single().Count).IsEqualTo(2);
        await AssertSingleError(ErrorMessageType.InteractionPermissionDeny);
    }

    [Test]
    public async Task ServerSideStoreAction_DoesNotAdvanceOrDeleteTheDoodad()
    {
        _shop.ToNextPhase = true;
        _store.Use(_character, _shop, 12087, -1);

        await Assert.That(_shop.ToNextPhase).IsFalse();
        await Assert.That(_character.CurrentInteractionObject).IsNull();
        await Assert.That(_session.Packets).IsEmpty();
    }

    private Npc AddNpc(List<uint> packs)
    {
        var npc = new Npc
        {
            ObjId = 77,
            ParentWorld = _character.ParentWorld,
            Template = new NpcTemplate { Merchant = true, MerchantPackIds = packs }
        };
        _character.ParentWorld.SetNpc(npc.ObjId, npc);
        return npc;
    }

    private PacketStream SendPurchase(uint clientShopId = 0, uint itemId = 1000,
        ShopCurrencyType currency = ShopCurrencyType.Money, uint npcId = 0, uint doodadId = 99,
        IReadOnlyList<StorePurchaseRequest> requests = null)
    {
        requests ??= [new(itemId, 2, currency)];
        var body = new PacketStream().WriteBc(npcId).WriteBc(doodadId).Write(clientShopId)
            .Write((byte)requests.Count).Write((byte)0);
        foreach (var request in requests)
            body.Write(request.ItemId).Write((byte)12).Write(request.Count).Write((byte)request.Currency);
        body.Write(false);
        var stream = new PacketStream(body.GetBytes());
        new CSBuyItemsPacket { Connection = _character.Connection }.Read(stream);
        return stream;
    }

    private async Task AssertUnchanged(ErrorMessageType error)
    {
        await Assert.That(_character.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_character.Money).IsEqualTo(1000L);
        await Assert.That(_character.HonorPoint).IsEqualTo(1000);
        await Assert.That(_character.VocationPoint).IsEqualTo(1000);
        await AssertSingleError(error);
    }

    private async Task AssertSingleError(ErrorMessageType expected)
    {
        await Assert.That(_session.Packets).HasSingleItem();
        await Assert.That(BitConverter.ToUInt16(_session.Packets[0], 6)).IsEqualTo(SCOffsets.SCErrorMsgPacket);
        await Assert.That(BitConverter.ToInt16(_session.Packets[0], 8)).IsEqualTo((short)expected);
    }

    private WorldInstance CreateWorld(uint id)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, id);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worlds)!;
        worlds[id] = world;
        return world;
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
