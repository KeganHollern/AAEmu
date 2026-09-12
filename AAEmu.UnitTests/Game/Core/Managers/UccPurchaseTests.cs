using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2S;
using AAEmu.Game.Core.Packets.S2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Stream;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class UccPurchaseTests
{
    private readonly Dictionary<Type, object> _singletons = [];
    private ItemManager _items;
    private UccManager _uccs;
    private CharacterMock _character;
    private Doodad _printer;
    private StreamConnection _connection;
    private Dictionary<ulong, Item> _allItems;
    private int _commits;
    private Func<bool> _commit;
    private DoodadFuncStampMaker _maker;

    [Before(Test)]
    public void SetUp()
    {
        _commits = 0;
        _commit = () => true;
        var itemIds = Mock.Of<IItemIdManager>();
        uint itemId = 100;
        itemIds.GetNextId().Returns(() => itemId++);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, itemIds.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        Install(_items);
        Install(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_removedItems", new List<ulong>());
        SetField(_items, "_templates", new Dictionary<uint, ItemTemplate>
        {
            [17663] = new UccTemplate { Id = 17663, MaxCount = 1, FixedGrade = 0 },
            [11127] = new ItemTemplate { Id = 11127, MaxCount = 100, FixedGrade = 0 }
        });
        var containers = new Dictionary<ulong, ItemContainer>();
        SetField(_items, "_allPersistentContainers", containers);
        _character = new CharacterMock { Id = 7, AccountId = 7, ObjId = 7, Name = "CrestMaker", Money = 100000,
            NumInventorySlots = 5, NumBankSlots = 5, Connection = new GameConnection(Mock.Of<ISession>().Object) };
        _character.Connection.ActiveChar = _character;
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_character, true);
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate) continue;
            var container = new ItemContainer(_character.Id, type, false, _character)
                { Owner = _character, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        _character.Inventory = new Inventory(_character);
        var worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(worlds);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 1, true, 1);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(new[] { new KeyValuePair<uint, WorldInstance>(1, world) }));
        _printer = new Doodad { ObjId = 99, TemplateId = 3038, ParentWorld = world };
        _printer.CurrentFuncs.Add(new DoodadFunc { FuncId = 2, FuncType = nameof(DoodadFuncStampMaker) });
        world.AddObject(_printer);
        _character.ParentWorld = world;
        _character.CurrentInteractionObject = _printer;
        _connection = new StreamConnection(Mock.Of<ISession>().Object) { GameConnection = _character.Connection };
        var uccIds = Mock.Of<IUccIdManager>();
        uint uccId = 500;
        uccIds.GetNextId().Returns(() => uccId++);
        _maker = new DoodadFuncStampMaker { Id = 2, ConsumeMoney = 50000, ConsumeItemId = 11127, ConsumeCount = 1, ItemId = 17663 };
        _uccs = new UccManager(uccIds.Object)
        {
            ResolveStampMaker = _ => _maker,
            CommitPurchase = (_, _) => { _commits++; return _commit(); }
        };
        _uccs.Patterns.Add(8, 3);
        _uccs.Patterns.Add(3, 4);
        Install(_uccs);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (type, instance) in _singletons)
            type.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, instance);
        _singletons.Clear();
    }

    [Test]
    public async Task SimpleCrest_ChargesAuthoredCopperOnceAndStoresCrestOnOutput()
    {
        var source = Simple();
        await Assert.That(_uccs.StartUpload(_connection, 99, 0, source)).IsTrue();
        await Assert.That(_character.Money).IsEqualTo(100000L);
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsTrue();
        var item = _character.Inventory.Bag.Items.Single();
        await Assert.That(item.UccId).IsEqualTo((ulong)source.Id);
        await Assert.That(_uccs.GetUccFromItem(item)).IsSameReferenceAs(source);
        await Assert.That(_character.Money).IsEqualTo(50000L);
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsFalse();
        await Assert.That(_commits).IsEqualTo(1);
        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CustomCrest_ConsumesAuthoredMaterialAndUsesFreedSlotWithoutCopper()
    {
        _character.Inventory.Bag.ContainerSize = 1;
        _character.Inventory.Bag.UpdateFreeSlotCount();
        var material = Material();
        var source = new CustomUcc();
        await Assert.That(StartCustom(source)).IsTrue();
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsTrue();
        await Assert.That(_character.Money).IsEqualTo(100000L);
        await Assert.That(_allItems.ContainsKey(material.Id)).IsFalse();
        var output = _character.Inventory.Bag.Items.Single();
        await Assert.That(output.TemplateId).IsEqualTo(17663u);
        await Assert.That(output.UccId).IsEqualTo((ulong)source.Id);
        await Assert.That(source.Data.Count).IsEqualTo(144);
    }

    [Test]
    [Arguments("funds")]
    [Arguments("full")]
    [Arguments("commit")]
    [Arguments("moved")]
    [Arguments("interaction")]
    [Arguments("replaced_character")]
    [Arguments("cancel")]
    public async Task ConfirmationFailure_LeavesWalletAndItemsUnchanged(string reason)
    {
        await Assert.That(_uccs.StartUpload(_connection, 99, 0, Simple())).IsTrue();
        if (reason == "funds") _character.Money = 49999;
        if (reason == "full") { _character.Inventory.Bag.ContainerSize = 0; _character.Inventory.Bag.UpdateFreeSlotCount(); }
        if (reason == "commit") _commit = () => false;
        if (reason == "moved") _character.Transform.Local.SetPosition(6, 0, 0);
        if (reason == "interaction") _character.CurrentInteractionObject = null;
        if (reason == "replaced_character") _connection.GameConnection.ActiveChar = new CharacterMock { Id = _character.Id };
        var money = _character.Money;
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, reason == "cancel" ? (byte)1 : (byte)0)).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(money);
        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(0);
        await Assert.That(_allItems.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("missing_material")]
    [Arguments("invalid_dds")]
    [Arguments("incomplete")]
    [Arguments("commit")]
    public async Task CustomFailure_PreservesMaterialAndWallet(string reason)
    {
        var material = reason == "missing_material" ? null : Material();
        var data = Dds();
        if (reason == "invalid_dds") data[0] = 0;
        await Assert.That(_uccs.StartUpload(_connection, 99, data.Length, new CustomUcc())).IsTrue();
        if (reason != "incomplete")
            await Assert.That(_uccs.UploadPart(_connection, new UccPart { Total = data.Length, Size = data.Length, Data = data })).IsTrue();
        if (reason == "commit") _commit = () => false;
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(100000L);
        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(material == null ? 0 : 1);
        if (material != null) await Assert.That(_character.Inventory.Bag.Items.Single()).IsSameReferenceAs(material);
    }

    [Test]
    [Arguments("printer")]
    [Arguments("interaction")]
    [Arguments("function")]
    [Arguments("background")]
    [Arguments("foreground")]
    [Arguments("color")]
    [Arguments("size")]
    [Arguments("negative")]
    public async Task InvalidStart_RejectsBeforeQueueOrMutation(string reason)
    {
        var source = Simple();
        if (reason == "interaction") _character.CurrentInteractionObject = null;
        if (reason == "function") _printer.CurrentFuncs.Clear();
        if (reason == "background") source.Pattern1 = 3;
        if (reason == "foreground") source.Pattern2 = 8;
        if (reason == "color") source.Color1R = 256;
        await Assert.That(_uccs.StartUpload(_connection, reason == "printer" ? 100u : 99u,
            reason == "size" ? 87537 : reason == "negative" ? -1 : 0, source)).IsFalse();
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsFalse();
        await Assert.That(_commits).IsEqualTo(0);
        await Assert.That(_character.Money).IsEqualTo(100000L);
    }

    [Test]
    public async Task DuplicateStart_PreservesFirstUploadAndDisconnectReleasesItsState()
    {
        var first = Simple();
        await Assert.That(_uccs.StartUpload(_connection, 99, 0, first)).IsTrue();
        await Assert.That(_uccs.StartUpload(_connection, 99, 0, Simple())).IsFalse();
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsTrue();
        await Assert.That(_uccs.GetUccFromItem(_character.Inventory.Bag.Items.Single())).IsSameReferenceAs(first);
        await Assert.That(_uccs.StartUpload(_connection, 99, 0, Simple())).IsTrue();
        _uccs.RemoveConnection(_connection);
        await Assert.That(_uccs.StartUpload(_connection, 99, 0, Simple())).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(14)]
    [Arguments(66)]
    [Arguments(68)]
    public async Task StartPacket_InvalidBodyLengthHasNoEffect(int length)
    {
        new CTStartUploadEmblemStreamPacket { Connection = _connection }.Read(new PacketStream(new byte[length]));
        await Assert.That(_commits).IsEqualTo(0);
        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("valid")]
    [Arguments("size")]
    [Arguments("index")]
    [Arguments("total")]
    [Arguments("truncated")]
    [Arguments("trailing")]
    [Arguments("over_limit")]
    public async Task UploadPacket_ChecksEveryPartFieldBeforeCompletion(string kind)
    {
        Material();
        var data = Dds();
        await Assert.That(_uccs.StartUpload(_connection, 99, data.Length, new CustomUcc())).IsTrue();
        var body = new PacketStream().Write(kind == "total" ? data.Length + 1 : data.Length)
            .Write(kind == "size" ? data.Length + 1 : data.Length).Write(kind == "index" ? 1u : 0u)
            .Write((ushort)(kind == "over_limit" ? 3097 : data.Length));
        foreach (var value in kind == "truncated" ? data[..^1] : data) body.Write(value);
        if (kind == "trailing") body.Write((byte)0);
        body.Rollback();
        new CTUploadEmblemStreamPacket { Connection = _connection }.Read(body);
        await Assert.That(_uccs.ConfirmDefaultUcc(_connection, 0)).IsEqualTo(kind == "valid");
        await Assert.That(_commits).IsEqualTo(kind == "valid" ? 1 : 0);
        await Assert.That(_character.Money).IsEqualTo(100000L);
    }

    [Test]
    public async Task StartPacket_ExactNativeBodyConsumesAllFieldsAndUsesFileTime()
    {
        var source = Simple();
        var body = new PacketStream().WriteBc(99).Write(0UL).Write(0);
        source.Write(body);
        body.Rollback();
        new CTStartUploadEmblemStreamPacket { Connection = _connection }.Read(body);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        var status = new PacketStream().Write((byte)0);
        status.Rollback();
        new CTEmblemStreamUploadStatusPacket { Connection = _connection }.Read(status);
        await Assert.That(_commits).IsEqualTo(1);
        await Assert.That(status.LeftBytes).IsEqualTo(0);
        var wire = new PacketStream();
        source.Write(wire);
        wire.Rollback();
        var decoded = new DefaultUcc();
        decoded.Read(wire);
        await Assert.That(decoded.Modified).IsEqualTo(source.Modified);
        await Assert.That(wire.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(EmblemStreamStatus.Continue)]
    [Arguments(EmblemStreamStatus.Start)]
    [Arguments(EmblemStreamStatus.End)]
    [Arguments(EmblemStreamStatus.Failed)]
    public async Task ReceiveStatus_WritesNativeStatusAndCount(EmblemStreamStatus status)
    {
        var body = new TCEmblemStreamRecvStatusPacket(status).Write(new PacketStream());
        body.Rollback();
        await Assert.That(body.ReadByte()).IsEqualTo((byte)status);
        await Assert.That(body.ReadInt32()).IsEqualTo(0);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    private bool StartCustom(CustomUcc source)
    {
        var data = Dds();
        return _uccs.StartUpload(_connection, 99, data.Length, source) &&
            _uccs.UploadPart(_connection, new UccPart { Total = data.Length, Size = data.Length, Data = data });
    }

    private Item Material()
    {
        var item = _items.Create(11127, 1, 0, true);
        item.OwnerId = _character.Id;
        item.SlotType = SlotType.Inventory;
        item.Slot = 0;
        item._holdingContainer = _character.Inventory.Bag;
        _character.Inventory.Bag.Items.Add(item);
        _character.Inventory.Bag.UpdateFreeSlotCount();
        return item;
    }

    internal static byte[] Dds(uint width = 1, uint height = 1, uint mipCount = 1, uint fourCc = 0x35545844)
    {
        var length = 128u;
        for (var mip = 0u; mip < mipCount; mip++)
            length += ((Math.Max(1, width >> (int)mip) + 3) / 4) * ((Math.Max(1, height >> (int)mip) + 3) / 4) *
                (fourCc == 0x31545844 ? 8u : 16u);
        var data = new byte[length];
        Put(data, 0, 0x20534444); Put(data, 4, 124); Put(data, 12, height); Put(data, 16, width);
        Put(data, 24, 1); Put(data, 28, mipCount); Put(data, 76, 32); Put(data, 80, 4); Put(data, 84, fourCc);
        return data;
    }
    internal static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static DefaultUcc Simple() => new() { Pattern1 = 8, Pattern2 = 3, Color1R = 255, Modified = DateTime.UtcNow };
    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private void Install<T>(T instance) where T : Singleton<T>
    {
        var type = typeof(Singleton<T>);
        var field = type.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add(type, field.GetValue(null));
        field.SetValue(null, instance);
    }
}
