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
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

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
            Template = new NpcTemplate { Merchant = true, MerchantPackId = 145 }
        };
        _character.ParentWorld.SetNpc(npc.ObjId, npc);
        SendPurchase(clientShopId: 999, npcId: 77, doodadId: 0);

        await Assert.That(_character.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(1000U);
        await Assert.That(_character.Money).IsEqualTo(980L);
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

    private PacketStream SendPurchase(uint clientShopId = 0, uint itemId = 1000,
        ShopCurrencyType currency = ShopCurrencyType.Money, uint npcId = 0, uint doodadId = 99)
    {
        var body = new PacketStream().WriteBc(npcId).WriteBc(doodadId).Write(clientShopId)
            .Write((byte)1).Write((byte)0)
            .Write(itemId).Write((byte)12).Write(2).Write((byte)currency).Write(false);
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
