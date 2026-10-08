using System.Collections.Concurrent;
using System.Numerics;
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
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class ClimbPacketTests
{
    private readonly Dictionary<FieldInfo, object> _singletons = [];
    private RecordingCharacter _character;
    private RecordingCharacter _other;
    private Doodad _tree;
    private RecordingGrowthPhase _phase;

    [Before(Test)]
    public void SetUp()
    {
        Install(new PermissionManager(null));
        var worlds = new WorldManager(null, null, null, null, null);
        Install(worlds);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            [new KeyValuePair<uint, WorldInstance>(1, world)]));
        var skills = new SkillManager(null, null);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate>());
        Install(skills);

        var doodads = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);
        SetField(doodads, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [2858] = [new() { FuncType = nameof(DoodadFuncClimb), FuncId = 252, NextPhase = -1 }]
        });
        _phase = new RecordingGrowthPhase();
        SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [2858] = [new() { FuncId = 1, FuncType = nameof(RecordingGrowthPhase) }]
        });
        SetField(doodads, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(RecordingGrowthPhase)] = new() { [1] = _phase }
        });
        Install(doodads);

        _character = new RecordingCharacter { Id = 7, ObjId = 0x010203, ParentWorld = world,
            Connection = new GameConnection(Mock.Of<ISession>().Object) };
        _character.Connection.ActiveChar = _character;
        _other = new RecordingCharacter { Id = 8, ObjId = 0x040506, ParentWorld = world };
        world.AddObject(_character);
        world.AddObject(_other);
        _tree = new Doodad { ObjId = 0xabcdef, TemplateId = 408, ParentWorld = world, FuncGroupId = 2858,
            GrowthTime = DateTime.UtcNow.AddHours(1), PlantTime = DateTime.UtcNow.AddDays(-2) };
        _tree.Transform.Local.Position = new Vector3(100, 200, 5);
        _character.Transform.Local.Position = new Vector3(101, 202, 7);
        world.AddObject(_tree);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.Reverse())
            field.SetValue(null, previous);
    }

    [Test]
    public async Task Hang_AttachesAndRelaysWithoutRestartingTheTreesGrowthPhase()
    {
        var deadline = _tree.GrowthTime;
        var position = _character.Transform.World.Position;
        var packet = new CSHangPacket { Connection = _character.Connection };
        // Native two-u24 body, without a skill or phase field.
        var body = new PacketStream(new byte[] { 3, 2, 1, 0xef, 0xcd, 0xab });
        packet.Read(body);

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0xcb);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.Transform.StickyParent).IsSameReferenceAs(_tree.Transform);
        await Assert.That(_character.Transform.World.Position).IsEqualTo(position);
        await Assert.That(_tree.FuncGroupId).IsEqualTo(2858u);
        await Assert.That(_tree.GrowthTime).IsEqualTo(deadline);
        await Assert.That(_phase.Calls).IsEqualTo(0);
        var (response, self) = _character.Packets.Single();
        await Assert.That(self).IsFalse();
        await Assert.That(response.TypeId).IsEqualTo((ushort)0x137);
        var output = new PacketStream(response.Write(new PacketStream()).GetBytes());
        await Assert.That(output.ReadBc()).IsEqualTo(_character.ObjId);
        await Assert.That(output.ReadBc()).IsEqualTo(_tree.ObjId);
        await Assert.That(output.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Hang_AnotherPlayersTreeDoesNotEnterTheTheftOrLootPath()
    {
        _tree.OwnerType = DoodadOwnerType.Character;
        _tree.OwnerId = _other.Id;
        _tree.PlantTime = DateTime.UtcNow;
        Hang(_character.ObjId, _tree.ObjId);
        await Assert.That(_character.Transform.StickyParent).IsSameReferenceAs(_tree.Transform);
        await Assert.That(_phase.Calls).IsEqualTo(0);
        await Assert.That(_tree.OwnerId).IsEqualTo(_other.Id);
    }

    [Test]
    [Arguments(0u)]
    [Arguments(0x040506u)]
    [Arguments(0xffffffu)]
    public async Task Hang_RejectsAnotherUnitOrAnInvalidAuthor(uint unitId)
    {
        Hang(unitId, _tree.ObjId);
        await Assert.That(_character.Transform.StickyParent).IsNull();
        await Assert.That(_other.Transform.StickyParent).IsNull();
        await Assert.That(_character.Packets).IsEmpty();
    }

    [Test]
    [Arguments(0u)]
    [Arguments(0x010203u)]
    [Arguments(0xffffffu)]
    public async Task Hang_RejectsMissingTargetsAndSelfAttachment(uint targetId)
    {
        Hang(_character.ObjId, targetId);
        await Assert.That(_character.Transform.StickyParent).IsNull();
        await Assert.That(_character.Packets).IsEmpty();
    }

    [Test]
    public async Task HangAndUnhang_RejectTruncatedBodiesAndTrailingData()
    {
        var hang = new CSHangPacket { Connection = _character.Connection };
        var unhang = new CSUnhangPacket { Connection = _character.Connection };
        var hangBody = new PacketStream().WriteBc(_character.ObjId).WriteBc(_tree.ObjId).GetBytes();
        var unhangBody = new PacketStream().WriteBc(_character.ObjId).Write(7u).GetBytes();
        for (var length = 0; length < hangBody.Length; length++)
            hang.Read(new PacketStream(hangBody[..length]));
        for (var length = 0; length < unhangBody.Length; length++)
            unhang.Read(new PacketStream(unhangBody[..length]));
        hang.Read(new PacketStream([.. hangBody, 0]));
        unhang.Read(new PacketStream([.. unhangBody, 0]));
        await Assert.That(_character.Transform.StickyParent).IsNull();
        await Assert.That(_character.Packets).IsEmpty();
    }

    [Test]
    [Arguments(0u)]
    [Arguments(2u)]
    [Arguments(7u)]
    public async Task Unhang_UsesTheSavedTargetAndPreservesTheNativeReason(uint reason)
    {
        Hang(_character.ObjId, _tree.ObjId);
        _character.Packets.Clear();
        var position = _character.Transform.World.Position;
        var packet = new CSUnhangPacket { Connection = _character.Connection };
        var body = new PacketStream(new PacketStream().WriteBc(_character.ObjId).Write(reason).GetBytes());
        packet.Read(body);
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0xcc);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.Transform.StickyParent).IsNull();
        await Assert.That(_character.Transform.Parent).IsNull();
        await Assert.That(_character.Transform.World.Position).IsEqualTo(position);
        var (response, self) = _character.Packets.Single();
        await Assert.That(self).IsFalse();
        await Assert.That(response.TypeId).IsEqualTo((ushort)0x138);
        var output = new PacketStream(response.Write(new PacketStream()).GetBytes());
        await Assert.That(output.ReadBc()).IsEqualTo(_character.ObjId);
        await Assert.That(output.ReadBc()).IsEqualTo(_tree.ObjId);
        await Assert.That(output.ReadUInt32()).IsEqualTo(reason);
        await Assert.That(output.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Unhang_AnotherCharacterCannotDetachTheClimber()
    {
        _other.Transform.StickyParent = _tree.Transform;
        var packet = new CSUnhangPacket { Connection = _character.Connection };
        packet.Read(new PacketStream(new PacketStream().WriteBc(_other.ObjId).Write(7u).GetBytes()));
        await Assert.That(_other.Transform.StickyParent).IsSameReferenceAs(_tree.Transform);
        await Assert.That(_character.Packets).IsEmpty();
    }

    [Test]
    public async Task HangAndUnhang_IgnoreAnInactiveConnection()
    {
        _character.Connection.ActiveChar = null;
        Hang(_character.ObjId, _tree.ObjId);
        new CSUnhangPacket { Connection = _character.Connection }.Read(
            new PacketStream(new PacketStream().WriteBc(_character.ObjId).Write(7u).GetBytes()));
        await Assert.That(_character.Transform.StickyParent).IsNull();
        await Assert.That(_character.Packets).IsEmpty();
    }

    private void Hang(uint unitId, uint targetId) => new CSHangPacket { Connection = _character.Connection }
        .Read(new PacketStream(new PacketStream().WriteBc(unitId).WriteBc(targetId).GetBytes()));

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingCharacter : CharacterMock
    {
        public List<(GamePacket Packet, bool Self)> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add((packet, self));
    }

    private sealed class RecordingGrowthPhase : DoodadPhaseFuncTemplate
    {
        public int Calls { get; private set; }
        public override bool Use(BaseUnit caster, Doodad owner)
        {
            Calls++;
            owner.GrowthTime = DateTime.UtcNow.AddDays(2);
            return false;
        }
    }
}
