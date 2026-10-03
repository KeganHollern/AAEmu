using System.Reflection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils.DB;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Slaves;

[NotInParallel]
public sealed class TurretAimTests
{
    private readonly Dictionary<FieldInfo, object> _instances = [];
    private WorldInstance _world;
    private ProbeSlave _slave;
    private Character _actor;
    private VehicleModel _model;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        var worlds = new WorldManager(null, null, null, null, null);
        SetInstance(worlds);
        _world = new WorldInstance(new WorldTemplate
        { CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 0);
        Set(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [0] = _world });
        _world.SlaveManager = new SlaveManager(_world);
        _actor = new ProbeCharacter { ObjId = 10, Hp = 100 };
        _slave = new ProbeSlave { ObjId = 0x123456, ModelId = 234, Hp = 100, Name = "Turret",
            Template = new SlaveTemplate { Mountable = true }, AttachPointId = -1 };
        SetWorld(_actor, _world);
        SetWorld(_slave, _world);
        _world.AddObject(_slave);
        Attach(AttachPointKind.Driver);
        _model = new VehicleModel { Id = 10, TurretPitchAngleMin = -20, TurretPitchAngleMax = 35,
            TurretYawAngleMin = -22, TurretYawAngleMax = 22 };
        var models = new ModelManager();
        Set(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
        { ["VehicleModel"] = new() { [10] = _model } });
        Set(models, "_modelTypes", new Dictionary<uint, ModelType>
        { [234] = new() { Id = 234, SubId = 10, SubType = "VehicleModel" } });
        SetInstance(models);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _instances)
            field.SetValue(null, value);
    }

    [Test]
    public async Task Request_UsesExactElevenByteBody()
    {
        var stream = Body(0.25f, -0.125f);
        await Assert.That(stream.Count).IsEqualTo(11);
        var request = CSTurretStatePacket.ReadRequest(stream);
        await Assert.That(request).IsEqualTo((0x123456u, 0.25f, -0.125f));
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        var bytes = Body(0.25f, -0.125f).GetBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            stream = new PacketStream(bytes[..length]);
            await Assert.That(() => CSTurretStatePacket.ReadRequest(stream)).Throws<MarshalException>();
            await Assert.That(stream.Pos).IsEqualTo(0);
        }
        await Assert.That(() => CSTurretStatePacket.ReadRequest(new PacketStream([.. bytes, 0])))
            .Throws<MarshalException>();
    }

    [Test]
    [Arguments(AttachPointKind.Driver)]
    [Arguments(AttachPointKind.Passenger0)]
    public async Task ActiveSeat_UpdatesObserversAndLateVisibility(AttachPointKind seat)
    {
        Attach(seat);
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _actor };
        var packet = new CSTurretStatePacket { Connection = connection };
        packet.Read(Body(0.25f, -0.125f));

        await Assert.That(_slave.Updates.Count).IsEqualTo(1);
        var update = new PacketStream(_slave.Updates.Single());
        await Assert.That(update.ReadBc()).IsEqualTo(_slave.ObjId);
        await CheckPosture(update, 0.25f, -0.125f);
        await Assert.That(update.LeftBytes).IsEqualTo(0);
        await Assert.That(_slave.TurretAim).IsEqualTo(new TurretAimState(0.25f, -0.125f));

        // This is the actual initial/late visibility packet, not a separate test serializer.
        var initial = new SCUnitStatePacket(_slave).Write(new PacketStream());
        initial.Rollback();
        initial.ReadBc();
        initial.ReadString();
        initial.ReadByte();
        initial.ReadUInt32(); // slave database ID
        initial.ReadUInt16(); // tl
        initial.ReadUInt32(); // template
        initial.ReadUInt32(); // summoner
        initial.ReadString();
        initial.ReadPosition();
        initial.ReadSingle(); // scale
        initial.ReadByte(); // level
        initial.ReadUInt32(); // model
        initial.ReadBytes(28 * 4); // empty equipment
        var custom = new UnitCustomModelParams();
        custom.Read(initial);
        initial.ReadBc();
        initial.ReadInt32();
        initial.ReadInt32();
        initial.ReadByte(); // parent attachment: System
        initial.ReadByte(); // no bonding
        await CheckPosture(initial, 0.25f, -0.125f);

        packet.Read(Body(0.25f, -0.125f));
        await Assert.That(_slave.Updates.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("missing-seat")]
    [Arguments("different-occupant")]
    [Arguments("wrong-seat")]
    [Arguments("wrong-parent")]
    [Arguments("world")]
    [Arguments("instance")]
    [Arguments("dead-actor")]
    [Arguments("dead-vehicle")]
    [Arguments("removed")]
    [Arguments("retired")]
    [Arguments("model")]
    public async Task InvalidAuthority_PreservesAim(string invalid)
    {
        await Assert.That(TurretAimControl.TryApply(_actor, _slave.ObjId, 0.2f, 0.1f)).IsTrue();
        switch (invalid)
        {
            case "missing-seat": _slave.AttachedCharacters.Clear(); break;
            case "different-occupant": _slave.AttachedCharacters[AttachPointKind.Driver] = new ProbeCharacter(); break;
            case "wrong-seat": _actor.AttachedPoint = AttachPointKind.Passenger0; break;
            case "wrong-parent": _actor.Transform.Parent = new Slave().Transform; break;
            case "world": SetWorld(_actor, new WorldInstance(new WorldTemplate(), 1, true, 1)); break;
            case "instance": _actor.Transform.InstanceId = 5; break;
            case "dead-actor": _actor.Hp = 0; break;
            case "dead-vehicle": _slave.Hp = 0; break;
            case "removed": _world.RemoveObject(_slave); break;
            case "retired": _slave.AttachmentsRetired = true; break;
            case "model": _slave.ModelId = 999; break;
        }
        await Assert.That(TurretAimControl.TryApply(_actor, _slave.ObjId, 0.3f, 0.2f)).IsFalse();
        await Assert.That(_slave.TurretAim).IsEqualTo(new TurretAimState(0.2f, 0.1f));
        await Assert.That(_slave.Updates.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Limits_UseAuthoredDegreesAndRejectNonFiniteOrOutOfRangeValues()
    {
        var minPitch = -20 * TurretAimControl.DegreesToRadians;
        var maxPitch = 35 * TurretAimControl.DegreesToRadians;
        var maxYaw = 22 * TurretAimControl.DegreesToRadians;
        await Assert.That(TurretAimControl.Accepts(_model, minPitch, -maxYaw)).IsTrue();
        await Assert.That(TurretAimControl.Accepts(_model, maxPitch, maxYaw)).IsTrue();
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue })
        {
            await Assert.That(TurretAimControl.TryApply(_actor, _slave.ObjId, bad, 0)).IsFalse();
            await Assert.That(TurretAimControl.TryApply(_actor, _slave.ObjId, 0, bad)).IsFalse();
        }
        await Assert.That(TurretAimControl.Accepts(_model, MathF.BitDecrement(minPitch), 0)).IsFalse();
        await Assert.That(TurretAimControl.Accepts(_model, MathF.BitIncrement(maxPitch), 0)).IsFalse();
        await Assert.That(TurretAimControl.Accepts(_model, 0, MathF.BitIncrement(maxYaw))).IsFalse();
        await Assert.That(_slave.TurretAim).IsEqualTo(new TurretAimState(0, 0));
        await Assert.That(_slave.Updates.Count).IsEqualTo(0);
        _model.TurretYawAngleMin = 23;
        await Assert.That(TurretAimControl.Accepts(_model, 0, 0)).IsFalse();
        _model.TurretYawAngleMin = float.NaN;
        await Assert.That(TurretAimControl.Accepts(_model, 0, 0)).IsFalse();
    }

    [Test]
    public async Task FullCircle_AcceptsBothNativeProducerRangesWithoutRewriting()
    {
        _model.InstalledTurret = true;
        _model.TurretYawAngleMin = -180;
        _model.TurretYawAngleMax = 180;
        await Assert.That(TurretAimControl.TryApply(_actor, _slave.ObjId, 0, -MathF.PI)).IsTrue();
        await Assert.That(_slave.TurretAim.Yaw).IsEqualTo(-MathF.PI); // normal mouse clamp permits -pi
        await Assert.That(TurretAimControl.Accepts(_model, 0, MathF.PI)).IsTrue();
        await Assert.That(TurretAimControl.Accepts(_model, 0, MathF.BitIncrement(MathF.PI))).IsFalse();
        _model.TurretYawAngleMin = 0;
        _model.TurretYawAngleMax = 360;
        await Assert.That(TurretAimControl.Accepts(_model, 0, -1)).IsTrue(); // keyboard wrap
        await Assert.That(TurretAimControl.Accepts(_model, 0, -MathF.PI)).IsFalse();
        await Assert.That(TurretAimControl.Accepts(_model, 0, MathF.Tau)).IsTrue(); // mouse clamp
        _model.InstalledTurret = false;
        await Assert.That(TurretAimControl.Accepts(_model, 0, -1)).IsFalse();
    }

    [Test]
    public async Task SqliteTextFalse_UsesNativeNumericZeroAndInitialAimIsNeutral()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 'f' AS turret_yaw_angle_max, 't' AS installed_turret";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        reader.Read();
        await Assert.That(reader.GetFloat("turret_yaw_angle_max")).IsEqualTo(0f);
        await Assert.That(reader.GetBoolean("installed_turret", true)).IsTrue();
        var posture = new PacketStream();
        Unit.ModelPosture(posture, _slave, 0, false);
        await CheckPosture(posture, 0, 0);
    }

    [Test]
    public async Task ExactClient_LoadsAllTurretLimitsAndTextFalseThroughProductionReader()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM vehicle_models";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        var count = 0;
        var textFalseCount = 0;
        var neutralCount = 0;
        while (reader.Read())
        {
            var model = new VehicleModel();
            ModelManager.ReadTurretSettings(reader, model);
            await Assert.That(TurretAimControl.Accepts(model,
                model.TurretPitchAngleMin * TurretAimControl.DegreesToRadians,
                model.TurretYawAngleMin * TurretAimControl.DegreesToRadians)).IsTrue();
            if (TurretAimControl.Accepts(model, 0, 0))
                neutralCount++;
            else
                await Assert.That(reader.GetUInt32("id")).IsEqualTo(8000001u);
            if (reader.GetValue("turret_yaw_angle_max") is string value && value == "f")
            {
                textFalseCount++;
                await Assert.That(model.TurretYawAngleMax).IsEqualTo(0f);
            }
            if (reader.GetUInt32("id") == 10)
            {
                await Assert.That(model.TurretPitchAngleMin).IsEqualTo(-20f);
                await Assert.That(model.TurretPitchAngleMax).IsEqualTo(35f);
                await Assert.That(model.TurretYawAngleMin).IsEqualTo(-22f);
                await Assert.That(model.TurretYawAngleMax).IsEqualTo(22f);
            }
            count++;
        }
        await Assert.That(count).IsEqualTo(63);
        await Assert.That(neutralCount).IsEqualTo(62);
        await Assert.That(textFalseCount).IsGreaterThan(0);
    }

    [Test]
    public async Task RealBroadcast_SendsChangedPostureToNearbyCharacterAndLateVisibility()
    {
        var region = new Region(_world, 0, 0, 0);
        Set(region, "_neighbors", new[] { region });
        _world.Template.ZoneKeyByRegions = new uint[16, 16];
        var observer = new ProbeCharacter { ObjId = 20, Hp = 100 };
        var session = new RecordingSession();
        observer.Connection = new GameConnection(session) { ActiveChar = observer };
        SetWorld(observer, _world);
        region.AddObject(observer);
        _slave.Region = region;
        _slave.UseRealBroadcast = true;
        await Assert.That(TurretAimControl.TryApply(_actor, _slave.ObjId, 0.3f, 0.2f)).IsTrue();
        var update = new PacketStream(session.Packets.Single(packet =>
            BitConverter.ToUInt16(packet, 6) == SCOffsets.SCUnitModelPostureChangedPacket)[8..]);
        await Assert.That(update.ReadBc()).IsEqualTo(_slave.ObjId);
        await CheckPosture(update, 0.3f, 0.2f);

        session.Packets.Clear();
        _slave.AddVisibleObject(observer);
        var initial = session.Packets.Single(packet =>
            BitConverter.ToUInt16(packet, 6) == SCOffsets.SCUnitStatePacket)[8..];
        var expected = new SCUnitStatePacket(_slave).Write(new PacketStream()).GetBytes();
        await Assert.That(initial.SequenceEqual(expected)).IsTrue();
    }

    private void Attach(AttachPointKind seat)
    {
        _slave.AttachedCharacters.Clear();
        _slave.AttachedCharacters[seat] = _actor;
        _actor.AttachedPoint = seat;
        _actor.Transform.Parent = _slave.Transform;
    }

    private static PacketStream Body(float pitch, float yaw) =>
        new PacketStream().WriteBc(0x123456).Write(pitch).Write(yaw);

    private static async Task CheckPosture(PacketStream stream, float pitch, float yaw)
    {
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)8);
        await Assert.That(stream.ReadBoolean()).IsFalse();
        await Assert.That(stream.ReadSingle()).IsEqualTo(pitch);
        await Assert.That(stream.ReadSingle()).IsEqualTo(yaw);
    }

    private static void SetWorld(GameObject obj, WorldInstance world) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(obj, world);

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private void SetInstance<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _instances.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private sealed class ProbeCharacter() : Character(null)
    {
        public override void AddVisibleObject(Character character) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class ProbeSlave : Slave
    {
        public bool UseRealBroadcast { get; set; }
        public List<byte[]> Updates { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            if (UseRealBroadcast)
                base.BroadcastPacket(packet, self);
            Updates.Add(packet.Write(new PacketStream()).GetBytes());
        }
    }

    private sealed class RecordingSession : ISession
    {
        internal List<byte[]> Packets { get; } = [];
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
