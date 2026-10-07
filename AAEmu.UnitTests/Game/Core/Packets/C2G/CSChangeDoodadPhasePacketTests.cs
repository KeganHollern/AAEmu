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
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class CSChangeDoodadPhasePacketTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private CharacterMock _character;
    private RecordingDoodad _doodad;
    private DoodadFunc _function;
    private RecordingPhaseFunction _phase;
    private WorldManager _worlds;
    private Dictionary<uint, List<DoodadFunc>> _functions;

    [Before(Test)]
    public void SetUp()
    {
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);
        _function = new DoodadFunc
        {
            GroupId = 18326,
            FuncKey = 14756,
            FuncId = 71,
            FuncType = nameof(DoodadFuncBubble),
            SkillId = 22584,
            NextPhase = 18329
        };
        _functions = new Dictionary<uint, List<DoodadFunc>> { [18326] = [_function], [18329] = [] };
        SetField(manager, "_funcsByGroups", _functions);
        _phase = new RecordingPhaseFunction();
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [18329] = [new() { GroupId = 18329, FuncId = 1, FuncType = nameof(RecordingPhaseFunction) }]
        });
        SetField(manager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(RecordingPhaseFunction)] = new() { [1] = _phase }
        });
        Install(manager);
        _worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(_worlds);
        var world = CreateWorld(1);
        _character = new CharacterMock
        {
            Id = 7,
            ObjId = 7,
            ParentWorld = world,
            Connection = new GameConnection(new RecordingSession())
        };
        _character.Connection.ActiveChar = _character;
        _doodad = new RecordingDoodad
        {
            ObjId = 0xabcdef,
            TemplateId = 6806,
            ParentWorld = world,
            OwnerId = _character.Id,
            FuncGroupId = 18326
        };
        world.AddObject(_doodad);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances.Reverse())
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(nameof(DoodadFuncBubble))]
    [Arguments(nameof(DoodadFuncOpenPaper))]
    public async Task Read_UsesTheNativeBodyAndRunsTheAuthoredPhaseOnce(string type)
    {
        _function.FuncType = type;
        var phaseEvents = new List<uint>();
        _doodad.ParentWorld.DoodadPhaseChanged += (_, phase) => phaseEvents.Add(phase);
        var packet = new CSChangeDoodadPhasePacket { Connection = _character.Connection };
        // Native layout for object 0xabcdef, skill 22584, phase 18329, row 14756.
        byte[] nativeBody = [0xef, 0xcd, 0xab, 0x38, 0x58, 0, 0, 0x99, 0x47, 0, 0, 0xa4, 0x39, 0, 0];
        var body = new PacketStream(nativeBody);
        packet.Read(body);
        packet.Read(new PacketStream(Body()));

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0xe9);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(18329u);
        await Assert.That(_phase.Calls).IsEqualTo(1);
        await Assert.That(phaseEvents).IsEquivalentTo(new uint[] { 18329 });
        await Assert.That(_doodad.Packets).HasSingleItem();
        var response = new PacketStream(_doodad.Packets[0].Write(new PacketStream()).GetBytes());
        await Assert.That(_doodad.Packets[0].TypeId).IsEqualTo(SCOffsets.SCDoodadPhaseChangedPacket);
        await Assert.That(response.ReadBc()).IsEqualTo(0xabcdefu);
        await Assert.That(response.ReadUInt32()).IsEqualTo(18329u);
        await Assert.That(response.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(response.ReadInt32()).IsEqualTo(-1);
        await Assert.That(response.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(response.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.CurrentInteractionObject).IsNull();
    }

    [Test]
    [Arguments(6167u, 16297u, 16295, 13413u, 98u, 20743u)]
    [Arguments(6162u, 16269u, 16271, 13514u, 100u, 16262u)]
    [Arguments(4374u, 10968u, 10743, 9911u, 80u, 17015u)]
    public async Task Read_AcceptsThePublishedBookRows(uint templateId, uint phase, int nextPhase,
        uint rowId, uint funcId, uint skillId)
    {
        _function.GroupId = phase;
        _function.NextPhase = nextPhase;
        _function.FuncKey = rowId;
        _function.FuncId = funcId;
        _function.SkillId = skillId;
        _function.FuncType = nameof(DoodadFuncOpenPaper);
        _functions[phase] = [_function];
        _doodad.TemplateId = templateId;
        _doodad.FuncGroupId = phase;

        Send(Body(skillId: skillId, phase: nextPhase, funcKey: rowId));

        await Assert.That(_doodad.FuncGroupId).IsEqualTo((uint)nextPhase);
        await Assert.That(_doodad.Packets).HasSingleItem();
    }

    [Test]
    public async Task Read_ConcurrentDuplicatesRunThePhaseOnce()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Send(Body()))));
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(18329u);
        await Assert.That(_phase.Calls).IsEqualTo(1);
        await Assert.That(_doodad.Packets).HasSingleItem();
    }

    [Test]
    public async Task Read_RejectsEveryTruncatedBodyAndTrailingDataBeforeChanges()
    {
        var bytes = Body();
        var packet = new CSChangeDoodadPhasePacket { Connection = _character.Connection };
        for (var length = 0; length < bytes.Length; length++)
            packet.Read(new PacketStream(bytes[..length]));
        packet.Read(new PacketStream([.. bytes, 0]));
        await AssertUnchanged();
    }

    [Test]
    [Arguments(0u, 22584u, 18329, 14756u)]
    [Arguments(0xffffffu, 22584u, 18329, 14756u)]
    [Arguments(0xabcdefu, 0u, 18329, 14756u)]
    [Arguments(0xabcdefu, 22585u, 18329, 14756u)]
    [Arguments(0xabcdefu, 22584u, -1, 14756u)]
    [Arguments(0xabcdefu, 22584u, 0, 14756u)]
    [Arguments(0xabcdefu, 22584u, 18326, 14756u)]
    [Arguments(0xabcdefu, 22584u, int.MaxValue, 14756u)]
    [Arguments(0xabcdefu, 22584u, int.MinValue, 14756u)]
    [Arguments(0xabcdefu, 22584u, 18329, 0u)]
    [Arguments(0xabcdefu, 22584u, 18329, 71u)]
    [Arguments(0xabcdefu, 22584u, 18329, uint.MaxValue)]
    public async Task Read_RejectsInvalidReferencesAndDoesNotConfuseFuncIdWithRowId(
        uint objectId, uint skillId, int phase, uint funcKey)
    {
        Send(Body(objectId, skillId, phase, funcKey));
        await AssertUnchanged();
    }

    [Test]
    [Arguments(nameof(DoodadFuncLootItem))]
    [Arguments(nameof(DoodadFuncFakeUse))]
    [Arguments(nameof(DoodadFuncStoreUi))]
    public async Task Read_RejectsUnconfirmedFunctionTypes(string type)
    {
        _function.FuncType = type;
        Send(Body());
        await AssertUnchanged();
    }

    [Test]
    public async Task Read_RejectsDifferentCurrentGroupAndInactiveFunction()
    {
        _function.GroupId++;
        Send(Body());
        _function.GroupId--;
        _functions[18326].Clear();
        Send(Body());
        await AssertUnchanged();
    }

    [Test]
    public async Task Read_EnforcesPermissionBeforePhaseChange()
    {
        _function.PermId = 1;
        _doodad.OwnerId = 99;
        Send(Body());
        await AssertUnchanged();
        _doodad.OwnerId = _character.Id;
        Send(Body());
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(18329u);
    }

    [Test]
    [Arguments(3.01f, 0f)]
    [Arguments(0f, 3.01f)]
    public async Task Read_RejectsHorizontalAndVerticalDistance(float x, float z)
    {
        _doodad.Transform.Local.SetPosition(x, 0, z);
        Send(Body());
        await AssertUnchanged();
    }

    [Test]
    public async Task Read_AcceptsTheCurrentThreeMetreServiceBoundary()
    {
        _doodad.Transform.Local.SetPosition(3, 0, 0);
        Send(Body());
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(18329u);
    }

    [Test]
    public async Task Read_RejectsAnotherWorldOrInstance()
    {
        var world = _doodad.ParentWorld;
        _doodad.ParentWorld = CreateWorld(2);
        Send(Body());
        _doodad.ParentWorld = world;
        _doodad.Transform.InstanceId++;
        Send(Body());
        await AssertUnchanged();
    }

    [Test]
    public async Task Read_RejectsPendingRemovalAndDeletedDoodads()
    {
        _doodad.Despawn = DateTime.UtcNow.AddSeconds(1);
        Send(Body());
        _doodad.Despawn = DateTime.MinValue;
        _doodad.MarkLaborDeletion(true);
        Send(Body());
        await AssertUnchanged();
    }

    [Test]
    public async Task Read_RejectsRemovedObjectsAndInactiveCharacters()
    {
        _doodad.ParentWorld.RemoveObject(_doodad);
        Send(Body());
        _character.Connection.ActiveChar = null;
        Send(Body());
        await AssertUnchanged();
    }

    [Test]
    public async Task Read_UsesTheReplacementObjectInsteadOfAStaleReference()
    {
        var world = _doodad.ParentWorld;
        world.RemoveObject(_doodad);
        var replacement = new RecordingDoodad
        {
            ObjId = _doodad.ObjId,
            ParentWorld = world,
            FuncGroupId = 18329
        };
        world.AddObject(replacement);
        Send(Body());
        await AssertUnchanged();
        await Assert.That(replacement.Packets).IsEmpty();
    }

    [Test]
    public async Task Read_AllowsTheNextAuthoredInteractionAfterTheClientReceivesTheNewPhase()
    {
        _functions[18329].Add(new DoodadFunc
        {
            GroupId = 18329,
            FuncKey = 14780,
            FuncId = 79,
            FuncType = nameof(DoodadFuncBubble),
            SkillId = 22584,
            NextPhase = 18326
        });
        Send(Body());
        Send(Body(phase: 18326, funcKey: 14780));
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(18326u);
        await Assert.That(_doodad.Packets.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(18329)]
    public async Task ServerSkill_DoesNotRepeatLocalUiOrApplyASeparatePhaseRequest(int nextPhase)
    {
        DoodadFuncTemplate[] templates = [new DoodadFuncBubble(), new DoodadFuncOpenPaper()];
        foreach (var template in templates)
        {
            _doodad.ToNextPhase = true;
            template.Use(_character, _doodad, 22584, nextPhase);
            await Assert.That(_doodad.ToNextPhase).IsFalse();
        }
        await AssertUnchanged();
    }

    private void Send(byte[] bytes) => new CSChangeDoodadPhasePacket
    { Connection = _character.Connection }.Read(new PacketStream(bytes));

    private static byte[] Body(uint objectId = 0xabcdef, uint skillId = 22584, int phase = 18329, uint funcKey = 14756) =>
        new PacketStream().WriteBc(objectId).Write(skillId).Write(phase).Write(funcKey).GetBytes();

    private async Task AssertUnchanged()
    {
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(18326u);
        await Assert.That(_phase.Calls).IsEqualTo(0);
        await Assert.That(_doodad.Packets).IsEmpty();
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

    private sealed class RecordingDoodad : Doodad
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }

    private sealed class RecordingPhaseFunction : DoodadPhaseFuncTemplate
    {
        public int Calls { get; private set; }
        public override bool Use(BaseUnit caster, Doodad owner)
        {
            Calls++;
            return false;
        }
    }

    private sealed class RecordingSession : ISession
    {
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
