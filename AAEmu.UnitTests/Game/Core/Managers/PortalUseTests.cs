using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using Portal = AAEmu.Game.Models.Game.Units.Portal;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class PortalUseTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private readonly DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private WorldManager _worldManager;
    private WorldInstance _world;
    private WorldInstance _otherWorld;
    private RecordingSession _session;
    private RecordingCharacter _character;
    private Portal _portal;

    [Before(Test)]
    public void SetUp()
    {
        _worldManager = new WorldManager(null, null, null, null, null);
        Install(_worldManager);
        Install(new SusManager(_worldManager));
        Install(new TrialManager());
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        Install(skills);
        _world = new WorldInstance(new WorldTemplate { Id = 0 }, 0, true, 0);
        _otherWorld = new WorldInstance(new WorldTemplate { Id = 8 }, 0, true, 123);
        SetField(_worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            new Dictionary<uint, WorldInstance> { [0] = _world, [123] = _otherWorld }));
        _session = new RecordingSession();
        var connection = new GameConnection(_session);
        _character = new RecordingCharacter { Id = 7, ObjId = 70, ParentWorld = _world, Connection = connection };
        _character.Portals = new CharacterPortals(_character);
        connection.ActiveChar = _character;
        _portal = new Portal
        {
            ObjId = 0x345678,
            TemplateId = 3891,
            Template = new NpcTemplate { Scale = 1 },
            ModelId = 308,
            OwnerId = _character.Id,
            ParentWorld = _world,
            Hp = 1,
            TeleportPosition = new Transform(null, null, 0, 0, 20, 30, 40, 0f)
        };
        _world.AddObject(_portal);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    [Arguments(1f, 1f, 1f, true)]
    [Arguments(1f, 1f, 1.001f, false)]
    [Arguments(0f, 0f, 2f, false)]
    [Arguments(-100f, 0f, 0f, false)]
    [Arguments(float.NaN, 0f, 0f, false)]
    public async Task UseRange_MatchesInclusiveNativeThreeDimensionalComparison(float x, float y, float z, bool expected)
    {
        _character.Transform.Local.Position = new Vector3(x, y, z);
        await Assert.That(_portal.CanUseFrom(_character)).IsEqualTo(expected);
    }

    [Test]
    public async Task UseRange_AppliesAuthoredModelAndScale()
    {
        _portal.Template.Scale = 3;
        _character.Transform.Local.Position = new Vector3(3, 0, 0);
        await Assert.That(_portal.CanUseFrom(_character)).IsTrue();
        _portal.ModelId = 123;
        await Assert.That(_portal.CanUseFrom(_character)).IsFalse();
    }

    [Test]
    [Arguments("distant")]
    [Arguments("foreign-instance")]
    [Arguments("removed")]
    [Arguments("replaced")]
    [Arguments("despawned")]
    [Arguments("dead")]
    [Arguments("unknown")]
    [Arguments("ordinary-npc")]
    public async Task UsePortal_InvalidObject_DoesNotTeleportOrStartCooldown(string reason)
    {
        var objectId = _portal.ObjId;
        switch (reason)
        {
            case "distant": _portal.Transform.Local.Position = new Vector3(100, 0, 0); break;
            case "foreign-instance": _portal.ParentWorld = _otherWorld; break;
            case "removed": _world.RemoveObject(_portal); break;
            case "replaced":
                _world.RemoveObject(_portal);
                _world.AddObject(new Npc { ObjId = objectId, ParentWorld = _world });
                break;
            case "despawned": _portal.Despawned = true; break;
            case "dead": _portal.Hp = 0; break;
            case "unknown": objectId = 0xffffff; break;
            case "ordinary-npc":
                objectId = 2;
                _world.AddObject(new Npc { ObjId = objectId, ParentWorld = _world });
                break;
        }

        PortalManager.UsePortal(_character, objectId, false, _now);

        await Assert.That(_character.Transform.World.Position).IsEqualTo(Vector3.Zero);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.InvalidPortal);
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(_now)).IsTrue();
    }

    [Test]
    [Arguments(true, false, false)]
    [Arguments(false, false, true)]
    [Arguments(true, true, true)]
    public async Task UsePacket_OwnerPreferenceComesFromTheVisitor(bool onlyMine, bool ownsPortal, bool permitted)
    {
        _portal.OwnerId = ownsPortal ? _character.Id : 99;
        var body = new PacketStream().WriteBc(_portal.ObjId).Write(onlyMine);
        new CSUsePortalPacket { Connection = _character.Connection }.Read(body);

        await Assert.That(body.Count).IsEqualTo(4);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.DisabledSetPosition).IsEqualTo(permitted);
        if (permitted)
            await Assert.That(_character.Transform.World.Position).IsEqualTo(new Vector3(20, 30, 40));
        else
            await Assert.That(LastError()).IsEqualTo(ErrorMessageType.NotMyPortal);
    }

    [Test]
    public async Task UsePacket_TruncatedAtEachByte_DoesNotChangeState()
    {
        var bytes = new PacketStream().WriteBc(_portal.ObjId).Write(true).GetBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            var body = new PacketStream().Write(bytes[..length]);
            await Assert.That(() => new CSUsePortalPacket { Connection = _character.Connection }.Read(body)).ThrowsException();
        }
        await Assert.That(_session.Packets.Count).IsEqualTo(0);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(_now)).IsTrue();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(255, false)]
    [Arguments(1, true)]
    public async Task UsePacket_InvalidBooleanOrTrailingByte_DoesNotChangeState(byte flag, bool trailingByte)
    {
        var body = new PacketStream().WriteBc(_portal.ObjId).Write(flag);
        if (trailingByte)
            body.Write((byte)0);
        new CSUsePortalPacket { Connection = _character.Connection }.Read(body);
        await Assert.That(_session.Packets.Count).IsEqualTo(0);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(_now)).IsTrue();
    }

    [Test]
    public async Task UsePortal_UpdatesServerPositionAndNotifiesTheClient()
    {
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now);
        await Assert.That(_character.Transform.World.Position).IsEqualTo(new Vector3(20, 30, 40));
        await Assert.That(_character.Transform.InstanceId).IsEqualTo(0u);
        await Assert.That(_session.Packets.Count).IsEqualTo(1);
        await Assert.That(Opcode(_session.Packets[0])).IsEqualTo(SCOffsets.SCTeleportUnitPacket);
        await Assert.That(_character.Broadcasts.Count).IsEqualTo(1);
        var used = _character.Broadcasts[0];
        await Assert.That(used).IsTypeOf<SCUnitPortalUsedPacket>();
        var body = used.Write(new PacketStream());
        await Assert.That(body.ReadBc()).IsEqualTo(_character.ObjId);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task UsePortal_CrossInstanceUsesInstanceIdAndKeepsDestinationCoordinates()
    {
        _portal.TeleportPosition = new Transform(null, null, 0, _otherWorld.Id, 20, 30, 40, 0f);
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now);
        await Assert.That(_character.Transform.InstanceId).IsEqualTo(123u);
        await Assert.That(_character.ParentWorld).IsSameReferenceAs(_otherWorld);
        await Assert.That(_character.Transform.World.Position).IsEqualTo(new Vector3(20, 30, 40));
        var load = _session.Packets.Single(packet => Opcode(packet) == SCOffsets.SCLoadInstancePacket);
        var body = new PacketStream().Write(load[8..]);
        await Assert.That(body.ReadUInt32()).IsEqualTo(123u);
    }

    [Test]
    public async Task UsePortal_LoadedVehicleCannotChangeInstanceAndDoesNotFreezeThePlayer()
    {
        _world.SlaveManager = new SlaveManager(_world);
        var slave = new Slave { ObjId = 11, ParentWorld = _world, Summoner = _character, Hp = 1 };
        slave.AttachedDoodads.Add(new Doodad { ItemTemplateId = 123 });
        _world.AddObject(slave);
        _portal.TeleportPosition = new Transform(null, null, 0, _otherWorld.Id, 20, 30, 40, 0f);
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
        await Assert.That(_character.ParentWorld).IsSameReferenceAs(_world);
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveEquipmentLoadedItem);
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(_now)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UsePortal_BackpackAndTrialRestrictionsStillApply(bool inCourt)
    {
        if (inCourt)
        {
            var trials = (ConcurrentDictionary<uint, TrialData>)typeof(TrialManager)
                .GetProperty("Trials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(TrialManager.Instance)!;
            trials[1] = new TrialData { DefendantId = _character.Id };
        }
        else
        {
            SetField(SkillManager.Instance, "_taggedBuffs", new Dictionary<uint, List<uint>>
                { [(uint)BuffConstants.TagOverburdened] = [777] });
            var effects = (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_character.Buffs)!;
            effects.Add(new Buff(_character, _character, null, new BuffTemplate { Id = 777 }, null, _now.UtcDateTime));
        }
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
        await Assert.That(LastError()).IsEqualTo(inCourt ? ErrorMessageType.CannotUsePortalInTrial : ErrorMessageType.CannotUsePortalWithBackpack);
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(_now)).IsTrue();
    }

    [Test]
    public async Task UsePortal_DelayRejectsAnotherPortalWithoutChangingPosition()
    {
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now);
        _character.DisabledSetPosition = false;
        _character.Portals.UseState.CompleteTeleport(_now.AddSeconds(5));
        _character.Transform.Local.Position = Vector3.Zero;
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now.AddSeconds(14.999));
        await Assert.That(_character.Transform.World.Position).IsEqualTo(Vector3.Zero);
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.CannotReusePortalInDelayTime);
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now.AddSeconds(15));
        await Assert.That(_character.Transform.World.Position).IsEqualTo(new Vector3(20, 30, 40));
    }

    [Test]
    public async Task TeleportEnded_StartsTheDelayAfterTheClientCompletesTravel()
    {
        PortalManager.UsePortal(_character, _portal.ObjId, true, _now);
        var body = new PacketStream().Write(0L).Write(0L).Write(0f).Write(new byte[16]);
        new CSTeleportEndedPacket { Connection = _character.Connection }.Read(body);
        var completedAt = DateTimeOffset.UtcNow;

        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
        await Assert.That(_character.Transform.World.Position).IsEqualTo(new Vector3(20, 30, 40));
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(completedAt.AddSeconds(9))).IsFalse();
        await Assert.That(_character.Portals.UseState.TryBeginTeleport(completedAt.AddSeconds(11))).IsTrue();
    }

    [Test]
    public async Task OpenPortal_UnknownIdRejectsBeforeAnyReagentOrSpawnDependency()
    {
        var manager = new PortalManager(null, null, null, null, null, null);
        manager.OpenPortal(_character, new SkillObjectPortalInfo { Id = 123 }, 3);
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.InvalidPortal);
        await Assert.That(_character.DisabledSetPosition).IsFalse();
    }

    [Test]
    [Arguments(3f, 0f, 0f, true)]
    [Arguments(-3f, 0f, 0f, true)]
    [Arguments(0f, -3f, 0f, true)]
    [Arguments(0f, 0f, 3f, true)]
    [Arguments(-3.01f, 0f, 0f, false)]
    [Arguments(3f, 3f, 0f, false)]
    [Arguments(0f, 0f, -3.01f, false)]
    [Arguments(float.NaN, 0f, 0f, false)]
    [Arguments(0f, float.PositiveInfinity, 0f, false)]
    public async Task OpenPortal_EntranceValidationUsesFiniteThreeDimensionalDistance(float x, float y, float z, bool expected)
    {
        var request = new SkillObjectPortalInfo { Id = 1, X = x, Y = y, Z = z };
        await Assert.That(PortalManager.IsValidEntrancePosition(_character, request, 3)).IsEqualTo(expected);
        if (!expected)
        {
            _character.Portals.DistrictPortals[1] = new AAEmu.Game.Models.Game.Portal { Id = 1 };
            // Null dependencies fail if an invalid request reaches reagent consumption or spawn.
            new PortalManager(null, null, null, null, null, null).OpenPortal(_character, request, 3);
            await Assert.That(LastError()).IsEqualTo(ErrorMessageType.NotNearToTarget);
        }
    }

    [Test]
    public void OpenPortalEffect_InvalidCasterOrSkillObjectDoesNotThrow()
    {
        var effect = new OpenPortalEffect { Distance = 3 };
        effect.Apply(_character, null, null, null, null, null, new SkillObject(), _now.UtcDateTime);
        effect.Apply(_portal, null, null, null, null, null, new SkillObjectPortalInfo(), _now.UtcDateTime);
    }

    private ErrorMessageType LastError()
    {
        var packet = _session.Packets.Last();
        return Opcode(packet) == SCOffsets.SCErrorMsgPacket
            ? (ErrorMessageType)BitConverter.ToInt16(packet, 8)
            : throw new InvalidOperationException("The last packet was not an error.");
    }

    private static ushort Opcode(byte[] packet) => BitConverter.ToUInt16(packet, 6);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private sealed class RecordingCharacter : Character
    {
        internal List<GamePacket> Broadcasts { get; } = [];
        internal RecordingCharacter() : base(null) { }
        public override void BroadcastPacket(GamePacket packet, bool self) => Broadcasts.Add(packet);
    }

    private sealed class RecordingSession : ISession
    {
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
