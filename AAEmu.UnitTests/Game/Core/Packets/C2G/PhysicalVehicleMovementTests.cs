using System.Collections.Concurrent;
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
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class PhysicalVehicleMovementTests
{
    private readonly List<(FieldInfo Field, object Previous)> _instances = [];
    private WorldInstance _world;
    private SusManager _sus;
    private Driver _driver;
    private Car _car;

    [Before(Test)]
    public void SetUp()
    {
        Install(new PermissionManager(Mock.Of<IAccountManager>().Object));
        Install(new SlaveGameData());
        var models = new ModelManager();
        typeof(ModelManager).GetField("_modelTypes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(models, new Dictionary<uint, ModelType>());
        typeof(ModelManager).GetField("_models", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(models, new Dictionary<string, Dictionary<uint, Model>>());
        Install(models);
        var worlds = new WorldManager(null, null, null, null, null);
        Install(worlds);
        _sus = new SusManager(worlds) { WriteBatch = _ => { } };
        Install(_sus);
        _world = new WorldInstance(new WorldTemplate
        { CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 0)
        { Regions = new Region[16, 16] };
        GC.SuppressFinalize(_world);
        typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(worlds, new ConcurrentDictionary<uint, WorldInstance> { [0] = _world });
        _world.SlaveManager = new SlaveManager(_world);
        _driver = new Driver { ObjId = 120, Hp = 100, ParentWorld = _world,
            AttachedPoint = AttachPointKind.Driver };
        _car = new Car { ObjId = 121, Hp = 100, ParentWorld = _world,
            Template = new SlaveTemplate { Mountable = true }, TlId = 22,
            VehicleVelocity = new Vector3(6, 0, 0) };
        _world.AddObject(_car);
        _car.AttachedCharacters[AttachPointKind.Driver] = _driver;
        _driver.Transform.Parent = _car.Transform;
        MovementValidation.Reset(_car.ObjId);
    }

    [After(Test)]
    public void TearDown()
    {
        MovementValidation.Reset(_car.ObjId);
        _driver.Transform.Parent = null;
        _sus.Dispose();
        foreach (var (field, value) in _instances.AsEnumerable().Reverse())
            field.SetValue(null, value);
    }

    [Test]
    public async Task AcceptedMovement_UpdatesVelocityAndStopClearsIt()
    {
        Move(0, -32767, 0, 32767);
        await Assert.That(_car.VehicleVelocity).IsEqualTo(new Vector3(-30, 0, 30));
        Move(0, 0, 0, 0);
        await Assert.That(_car.VehicleVelocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task OtherDriver_CannotReplaceAcceptedVelocity()
    {
        _car.AttachedCharacters[AttachPointKind.Driver] = new Driver { ObjId = 200 };
        Move(0, 0, 0, 0);
        await Assert.That(_car.VehicleVelocity).IsEqualTo(new Vector3(6, 0, 0));
    }

    [Test]
    public async Task RejectedPosition_DoesNotReplaceLastAcceptedVelocity()
    {
        Move(0, 32767, 0, 0);
        // The first 2 movement anomalies are tolerated by the existing movement guard.
        Move(200, -32767, 0, 0);
        Move(400, 32767, 0, 0);
        await Assert.That(_car.VehicleVelocity).IsEqualTo(new Vector3(30, 0, 0));
        Move(600, 0, 0, 0);
        await Assert.That(_car.VehicleVelocity).IsEqualTo(new Vector3(30, 0, 0));
        await Assert.That(_car.Transform.Local.Position.X).IsEqualTo(400f);
    }

    [Test]
    public async Task DriverRebind_DoesNotInheritPreviousControlVelocity()
    {
        _world.SlaveManager.UnbindSlave(_driver, _car.TlId, AttachUnitReason.None);
        await Assert.That(_driver.AttachedPoint).IsEqualTo(AttachPointKind.None);
        _world.SlaveManager.BindSlave(_driver, _car.ObjId, AttachPointKind.Driver, AttachUnitReason.None);
        await Assert.That(_driver.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
        await Assert.That(_car.VehicleVelocity).IsEqualTo(Vector3.Zero);
    }

    private void Move(float x, short vx, short vy, short vz)
    {
        var body = new PacketStream().WriteBc(_car.ObjId).Write((byte)MoveTypeEnum.Vehicle);
        new VehicleMoveType { X = x, VelX = vx, VelY = vy, VelZ = vz }.Write(body);
        body.Rollback();
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _driver };
        var packet = new CSMoveUnitPacket { Connection = connection };
        packet.Read(body);
        packet.Execute();
    }

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _instances.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private sealed class Driver() : Character(null)
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }

    private sealed class Car : Slave
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }
}
