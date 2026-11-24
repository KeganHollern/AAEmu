using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Transfers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed partial class CarriageBoardingTests
{
    private static readonly WorldSpawnPosition Spawn = new() { X = 12609.46f, Y = 15359.16f, Z = 158.5336f };
    private static readonly WorldSpawnPosition Start = new() { X = 12738.3428f, Y = 15307.42065f, Z = 162.12067f, ZoneId = 195 };
    private static readonly WorldSpawnPosition Next = new() { X = 12744.7344f, Y = 15302.99268f, Z = 164.20215f, ZoneId = 195 };
    private readonly Dictionary<FieldInfo, object> _singletons = [];
    private WorldConfig _originalConfig;

    [Before(Test)]
    public void RememberConfig() => _originalConfig = AppConfiguration.Instance.World;

    [After(Test)]
    public void RestoreManagers()
    {
        AppConfiguration.Instance.World = _originalConfig;
        foreach (var (field, value) in _singletons)
            field.SetValue(null, value);
    }

    [Test]
    public async Task Carriage6_DepartsWithBothSeatsAtTheAuthoredSocket()
    {
        var world = SetUpStation();
        var template = Motor(654, AttachPointKind.Trailed0, 46).Template;
        template.TransferAllPaths.Add(new TransferPaths { Id = 191, OwnerId = 6, PathName = "lily_solzreed_in2" });
        template.TransferRoads.Add(new TransferRoads { Name = "lily_solzreed_in2", Pos = [Start, Next] });
        ConfigureSpawn(template);
        world.TransferManager = new TransferManager();
        var spawner = new TransferSpawner { ParentWorld = world, Position = Spawn, UnitId = 6 };
        var motor = spawner.Spawn(0);
        var cabin = motor.Bounded;
        var seat0 = cabin.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Passenger0);
        var seat1 = cabin.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Passenger1);
        Console.WriteLine($"Before the first movement, motor {motor.ObjId} at {motor.Transform.World.Position}, " +
            $"cabin {cabin.ObjId} at {cabin.Transform.World.Position} bonded to {cabin.BondingObjId}, " +
            $"seat 0 {seat0.ObjId} at {seat0.Transform.World.Position} parent {seat0.ParentObjId}, " +
            $"seat 1 {seat1.ObjId} at {seat1.Transform.World.Position} parent {seat1.ParentObjId}.");
        await Assert.That(cabin.ModelId).IsEqualTo(653u);
        await Assert.That(cabin.AttachPointId).IsEqualTo(AttachPointKind.Trailed0);
        await Assert.That(cabin.BondingObjId).IsEqualTo(motor.ObjId);
        await Assert.That(seat0.ParentObjId).IsEqualTo(cabin.ObjId);
        await Assert.That(seat1.ParentObjId).IsEqualTo(cabin.ObjId);

        await CheckPoint(motor.Transform.World.Position, new Vector3(Start.X, Start.Y, Start.Z));
        await CheckPoint(cabin.Transform.World.Position, new Vector3(12730.744f, 15312.685f, 163.37143f));
        await CheckSeats(motor, cabin, seat0, seat1);

        var passenger = new CharacterMock { Id = 10, ObjId = 100, ParentWorld = world };
        await BoardAndLeave(passenger, seat0);
        await BoardAndLeave(passenger, seat1);

        await Assert.That(motor.TimeLeft).IsGreaterThan(0u);
        await CheckPoint(motor.Transform.World.Position, new Vector3(Start.X, Start.Y, Start.Z));
        motor.MoveTo(motor);
        await CheckPoint(cabin.Transform.World.Position, new Vector3(12730.744f, 15312.685f, 163.37143f));

        motor.WaitTime = DateTime.MinValue;
        motor.MoveTo(motor); // The first cyclic checkpoint also has a station wait.
        motor.WaitTime = DateTime.MinValue;
        motor.MoveTo(motor);
        await Assert.That(motor.Transform.World.Position.X).IsGreaterThan(Start.X);
        await CheckSeats(motor, cabin, seat0, seat1);

        // The next segment turns north. The two seat positions must rotate with the model.
        motor.vTarget = motor.Transform.World.Position + new Vector3(0, 25, 0);
        motor.MoveTo(motor);
        await CheckPoint(cabin.Transform.World.Position, motor.Transform.World.Position + new Vector3(0, -9.24417f, 1.2507583f));
        await CheckSeats(motor, cabin, seat0, seat1);
        await BoardAndLeave(passenger, seat0);
        await BoardAndLeave(passenger, seat1);
    }

    [Test]
    public async Task PassengerBindingOnMotor654_StillUsesItsOwnGroundAndRoute()
    {
        var world = SetUpStation();
        var motorTemplate = Motor(654, AttachPointKind.Passenger0, 46).Template;
        motorTemplate.Id = 24;
        motorTemplate.WaitTime = 10;
        var first = new WorldSpawnPosition { X = 6868.6814f, Y = 12876.4597f, Z = 814.61157f, ZoneId = 154 };
        var next = new WorldSpawnPosition { X = 6837.1708f, Y = 12900.981f, Z = 828.95984f, ZoneId = 154 };
        motorTemplate.TransferAllPaths.Add(new TransferPaths { Id = 349, OwnerId = 24, PathName = "carriage_cradle_barefoot_in" });
        motorTemplate.TransferRoads.Add(new TransferRoads { Name = "carriage_cradle_barefoot_in", Pos = [first, next] });
        ConfigureSpawn(motorTemplate);
        world.TransferManager = new TransferManager();
        var spawn = new WorldSpawnPosition { X = 7485.59f, Y = 12620.1f, Z = 761.6985f };
        var motor = new TransferSpawner { ParentWorld = world, Position = spawn, UnitId = 24 }.Spawn(0);
        var expected = spawn.AsPositionVector() - Vector3.UnitY * 9.24417f;
        expected.Z = world.Template.GeoData.GetHeight(expected);
        await CheckPoint(motor.Transform.World.Position, first.AsPositionVector());
        await CheckPoint(motor.Bounded.Transform.World.Position, expected);
        await Assert.That(motor.Bounded.AttachPointId).IsEqualTo(AttachPointKind.Passenger0);
        await Assert.That(motor.Bounded.BondingObjId).IsEqualTo(motor.ObjId);
        await Assert.That(motor.Bounded.AttachedDoodads.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(123u, "safari_1", 654u, 20111.8713f, 27249.229f, 113.85671f, 20117.4846f, 27220.8556f, 112.99648f)]
    [Arguments(124u, "safari_6", 661u, 20462.0981f, 27158.4453f, 137.02292f, 20487.6902f, 27181.3293f, 139.74445f)]
    public async Task GhostSafari_ChairAndTelescopeFollowTheirCabin(uint id, string path, uint pathId,
        float startX, float startY, float startZ, float nextX, float nextY, float nextZ)
    {
        var world = SetUpStation(cells: 28, modelId: 1463, vehicleModelId: 57);
        var template = Motor(1463, AttachPointKind.Trailed0, 122).Template;
        template.Id = id;
        var first = new WorldSpawnPosition { X = startX, Y = startY, Z = startZ, ZoneId = 301 };
        var next = new WorldSpawnPosition { X = nextX, Y = nextY, Z = nextZ, ZoneId = 301 };
        template.TransferAllPaths.Add(new TransferPaths { Id = pathId, OwnerId = id, PathName = path,
            WaitTimeStart = id == 123 ? 30 : 0, WaitTimeEnd = 150 });
        template.TransferRoads.Add(new TransferRoads { Name = path, Pos = [first, next] });
        ConfigureSpawn(template, cabinModelId: 1459, seatId: 7156, phaseId: 19351, rear: (7959, 21906));
        world.TransferManager = new TransferManager();
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == id);
        spawner.ParentWorld = world;
        var motor = spawner.Spawn(0);
        var cabin = motor.Bounded;
        var chair = cabin.AttachedDoodads.Single(d => d.TemplateId == 7156);
        var telescope = cabin.AttachedDoodads.Single(d => d.TemplateId == 7959);
        await Assert.That(cabin.TemplateId).IsEqualTo(122u);
        await Assert.That(cabin.ModelId).IsEqualTo(1459u);
        await Assert.That(cabin.AttachPointId).IsEqualTo(AttachPointKind.Trailed0);
        await Assert.That(chair.AttachPoint).IsEqualTo(AttachPointKind.Passenger0);
        await Assert.That(telescope.AttachPoint).IsEqualTo(AttachPointKind.Passenger1);
        await CheckPoint(motor.Transform.World.Position, first.AsPositionVector());
        // The head mesh faces +Y. Both signed head meshes place $trailed0 9.24417 m behind the motor.
        var forward = Vector2.Normalize(new Vector2(nextX - startX, nextY - startY));
        var expected = first.AsPositionVector() + new Vector3(-forward.X * 9.24417f, -forward.Y * 9.24417f, 1.2507599f);
        var distance = Vector2.Distance(new(cabin.Transform.World.Position.X, cabin.Transform.World.Position.Y), new(expected.X, expected.Y));
        Console.WriteLine($"Safari {id} initial cabin XY error {distance:F3} m. Motor {motor.Transform.World.Position}, cabin {cabin.Transform.World.Position}, expected {expected}.");
        await CheckPoint(cabin.Transform.World.Position, expected);
        await CheckSeats(motor, cabin, chair, telescope);
        var passenger = new CharacterMock { Id = 10, ObjId = 100, ParentWorld = world };
        await BoardAndLeave(passenger, chair, 23464, AttachPointKind.None);
        await Assert.That(motor.TimeLeft).IsGreaterThan(0u);
        motor.MoveTo(motor);
        await CheckPoint(cabin.Transform.World.Position, expected);
        motor.WaitTime = DateTime.MinValue;
        motor.MoveTo(motor);
        motor.WaitTime = DateTime.MinValue;
        motor.MoveTo(motor);
        await Assert.That(motor.Transform.World.Position.X).IsGreaterThan(startX);
        await CheckSeats(motor, cabin, chair, telescope);
    }

    [Test]
    [Arguments(5u, 28u, "tiger01", 19u)]
    [Arguments(26u, 42u, "tiger03", 129u)]
    [Arguments(27u, 43u, "tiger04", 141u)]
    public async Task TigerCart_TrailerUsesTheStockSocketInBothDirections(uint motorId, uint trailerId, string routeName, uint routeId)
    {
        // These are the first three signed stock points. The short fixture reverses at the third point.
        Vector3[] samples = motorId switch
        {
            5 => [new(22085.93469f, 8411.0393f, 433.98453f), new(22087.02539f, 8412.5343f, 433.98389f), new(22089.51367f, 8416.0248f, 433.98154f)],
            26 => [new(22207.93158f, 8578.9711f, 525.12854f), new(22211.52545f, 8584.0393f, 525.14624f), new(22212.45691f, 8585.1235f, 525.57666f)],
            27 => [new(22295.90173f, 8700.0212f, 620.07593f), new(22297.39502f, 8702.079f, 620.08459f), new(22298.620f, 8703.7202f, 620.51428f)],
            _ => throw new ArgumentOutOfRangeException(nameof(motorId))
        };
        var world = SetUpStation(cells: 23, modelId: 565, vehicleModelId: 17);
        var template = Motor(565, AttachPointKind.Trailed0, trailerId).Template;
        template.Id = motorId;
        template.Cyclic = false;
        template.WaitTime = 10;
        var path = samples.Select(p => new WorldSpawnPosition { X = p.X, Y = p.Y, Z = p.Z, ZoneId = 258 }).ToList();
        template.TransferAllPaths.Add(new TransferPaths { Id = routeId, OwnerId = motorId, PathName = routeName,
            WaitTimeStart = 10, WaitTimeEnd = 10 });
        template.TransferRoads.Add(new TransferRoads { Name = routeName, Pos = path });
        var trailerTemplate = ConfigureSpawn(template, cabinModelId: 565);
        trailerTemplate.TransferBindingDoodads.Clear();

        world.TransferManager = new TransferManager();
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == motorId);
        spawner.ParentWorld = world;
        var motor = spawner.Spawn(0);
        var trailer = motor.Bounded;
        await Assert.That(trailer.TemplateId).IsEqualTo(trailerId);
        await Assert.That(trailer.BondingObjId).IsEqualTo(motor.ObjId);
        await Assert.That(trailer.AttachPointId).IsEqualTo(AttachPointKind.Trailed0);
        await Assert.That(trailer.AttachedDoodads.Count).IsEqualTo(0);

        // On these straight segments the first and second cart keep their facing when their direction of travel changes.
        var direction = samples[1] - samples[0];
        var forward = Vector2.Normalize(new Vector2(direction.X, direction.Y));
        var offset = new Vector3(-7.629541f * forward, 0);
        await CheckPoint(motor.Transform.World.Position, samples[0]);
        var parkedError = trailer.Transform.World.Position - (motor.Transform.World.Position + offset);
        Console.WriteLine($"Rail cart {motorId}: initial trailer XY error {new Vector2(parkedError.X, parkedError.Y).Length():F3} m.");
        await CheckPoint(trailer.Transform.World.Position, motor.Transform.World.Position + offset);
        var sawForward = false;
        var sawReverse = false;
        for (var step = 0; step < 120 && !sawReverse; step++)
        {
            var before = motor.Transform.World.Position;
            motor.WaitTime = DateTime.MinValue;
            motor.MoveTo(motor);
            var after = motor.Transform.World.Position;
            var facing = Vector3.Transform(Vector3.UnitY, motor.Transform.World.ToQuaternion());
            await Assert.That(Vector3.Dot(facing, new Vector3(forward, 0))).IsGreaterThan(0.95f);
            await CheckPoint(trailer.Transform.World.Position, after - 7.629541f * facing);
            if (Vector3.Distance(after, before) < 0.005f)
                continue;
            var travel = Vector2.Dot(new Vector2(after.X - before.X, after.Y - before.Y), forward);
            sawForward |= travel > 0;
            sawReverse |= motor.Reverse && travel < 0;
        }
        await Assert.That(sawForward).IsTrue();
        await Assert.That(sawReverse).IsTrue();
    }

    [Test]
    public async Task SingleTigerCart_ObserverAndMovementFaceTheRailsInBothDirections()
    {
        var world = SetUpStation(cells: 23, modelId: 565, vehicleModelId: 17);
        ModelManager.Instance.GetVehicleModels(565).Velocity = 13;
        // The stock compact contains trailer 29 but gives motor 25 no binding.
        var template = Motor(565, AttachPointKind.Trailed0, 29).Template;
        template.Id = 25;
        template.Name = "Tiger Cart";
        template.Cyclic = false;
        template.WaitTime = 10;
        template.PathSmoothing = 1;
        ConfigureSpawn(template, cabinModelId: 565);
        template.TransferBindings.Clear();
        SetSingleton(new NameManager());
        // The first four signed tiger02 points form a short return route.
        List<WorldSpawnPosition> path =
        [
            new() { X = 22137.22485f, Y = 8481.6746f, Z = 481.34695f, ZoneId = 258 },
            new() { X = 22137.96777f, Y = 8482.7140f, Z = 481.49744f, ZoneId = 258 },
            new() { X = 22138.87878f, Y = 8483.9543f, Z = 482.01910f, ZoneId = 258 },
            new() { X = 22139.95728f, Y = 8485.4526f, Z = 482.66196f, ZoneId = 258 }
        ];
        template.TransferAllPaths.Add(new TransferPaths { Id = 117, OwnerId = 25, PathName = "tiger02",
            WaitTimeStart = 10, WaitTimeEnd = 10 });
        template.TransferRoads.Add(new TransferRoads { Name = "tiger02", Pos = path });
        world.TransferManager = new TransferManager();
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == 25);
        spawner.ParentWorld = world;
        var motor = spawner.Spawn(0);
        await Assert.That(world.TransferManager.GetTransfers().Single()).IsSameReferenceAs(motor);
        await Assert.That(motor.Bounded).IsNull();
        await Assert.That(motor.TimeLeft).IsGreaterThan(0u);
        await CheckPoint(motor.Transform.World.Position, path[0].AsPositionVector());
        var session = new TransferVisibilityTests.RecordingSession();
        var viewer = new CharacterMock { ObjId = 400, ParentWorld = world, Connection = new GameConnection(session) };
        viewer.Transform.Local.SetPosition(path[0].AsPositionVector());
        world.GetRegionByPos(motor.Transform.World.Position).AddObject(viewer);
        await CheckObserverHeading(motor, viewer, session, path[1].AsPositionVector() - path[0].AsPositionVector());

        var sawForward = false;
        var sawReverse = false;
        for (var ticks = 0; ticks < 100 && !sawReverse; ticks++)
        {
            session.Packets.Clear();
            motor.WaitTime = DateTime.MinValue;
            var before = motor.Transform.World.Position;
            motor.MoveTo(motor);
            var travel = motor.Transform.World.Position - before;
            if (travel.LengthSquared() < 0.0001f)
                continue;
            var direction = motor.Reverse ? -travel : travel;
            var packet = session.Packets.Single();
            await Assert.That(BitConverter.ToUInt16(packet, 6)).IsEqualTo(SCOffsets.SCOneUnitMovementPacket);
            var stream = new PacketStream(packet, 8, packet.Length - 8);
            await Assert.That(stream.ReadBc()).IsEqualTo(motor.ObjId);
            await Assert.That(stream.ReadByte()).IsEqualTo((byte)MoveTypeEnum.Transfer);
            var movement = new TransferData();
            movement.Read(stream);
            await Assert.That(movement.Reverse).IsEqualTo(motor.Reverse);
            await Assert.That(Vector3.Distance(new Vector3(movement.X, movement.Y, movement.Z), motor.Transform.World.Position)).IsLessThan(0.02f);
            var facing = Vector3.Transform(Vector3.UnitY, movement.Rot);
            await CheckHeading(direction, facing);
            await CheckObserverHeading(motor, viewer, session, direction);
            sawForward |= !motor.Reverse;
            sawReverse |= motor.Reverse;
        }
        await Assert.That(sawForward && sawReverse).IsTrue();
    }

    private static async Task CheckObserverHeading(Transfer motor, CharacterMock viewer,
        TransferVisibilityTests.RecordingSession session, Vector3 forward)
    {
        // Parse the ordinary visibility notification for this unarmed, unparented transfer.
        session.Packets.Clear();
        motor.AddVisibleObject(viewer);
        var packet = session.Packets.Single();
        await Assert.That(BitConverter.ToUInt16(packet, 6)).IsEqualTo(SCOffsets.SCUnitStatePacket);
        var state = new PacketStream(packet, 8, packet.Length - 8);
        await Assert.That(state.ReadBc()).IsEqualTo(motor.ObjId);
        state.ReadString(); // name
        state.ReadByte(); // unit type
        state.ReadUInt16(); // transfer link id
        await Assert.That(state.ReadUInt32()).IsEqualTo(motor.TemplateId);
        state.ReadString(); // owner name
        var (x, y, z) = state.ReadPosition();
        await Assert.That(Vector3.Distance(new Vector3(x, y, z), motor.Transform.World.Position)).IsLessThan(0.02f);
        state.ReadSingle(); // scale
        state.ReadByte(); // level
        await Assert.That(state.ReadUInt32()).IsEqualTo(motor.ModelId);
        state.ReadBytes(7 * sizeof(uint)); // empty equipment body parts
        state.ReadByte(); // model params type
        state.ReadBc(); // unknown unit id
        state.ReadInt32(); // health
        state.ReadInt32(); // mana
        await Assert.That(state.ReadByte()).IsEqualTo((byte)AttachPointKind.System);
        state.ReadSByte(); // second attachment
        state.ReadByte(); // turret posture
        state.ReadBoolean(); // loot
        state.ReadSingle(); // turret pitch
        state.ReadSingle(); // turret yaw
        state.ReadByte(); // active weapon
        state.ReadByte(); // learned skills
        state.ReadInt32(); // passive buffs
        var rotation = PositionAndRotation.FromRollPitchYawSBytes(state.ReadSByte(), state.ReadSByte(), state.ReadSByte());
        await CheckHeading(forward, new Vector3(-MathF.Sin(rotation.Z), MathF.Cos(rotation.Z), 0));
    }

    private static async Task CheckHeading(Vector3 direction, Vector3 facing)
    {
        var expected = Vector2.Normalize(new Vector2(direction.X, direction.Y));
        await Assert.That(Vector2.Dot(expected, new Vector2(facing.X, facing.Y))).IsGreaterThan(0.999f);
    }

    [Test]
    public async Task CarriageWithoutRoute_UsesThePlacedMotorPoseIncludingRollAndPitch()
    {
        var motor = Motor(654, AttachPointKind.Trailed0, 46);
        var spawn = new WorldSpawnPosition { X = 150, Y = 160, Z = 0, Roll = 0.2f, Pitch = -0.1f, Yaw = 0.8f };
        var cabin = new Transfer { ModelId = 653 };
        motor.InitializeRoutePose(spawn);
        motor.PlaceTrailer(cabin, motor.TrailerSocket.Value, moving: false);
        var rotation = motor.Transform.Local.ToQuaternion();
        var socket = Vector3.Transform(new Vector3(0, -9.24417f, 1.2507583f), rotation);
        await CheckPoint(cabin.Transform.World.Position, new Vector3(150, 160, 0) + socket);
        await Assert.That(cabin.Rot).IsEqualTo(rotation);
    }

    [Test]
    [Arguments(654u, AttachPointKind.Passenger0, 46u)]
    [Arguments(1463u, AttachPointKind.Passenger0, 122u)]
    [Arguments(1463u, AttachPointKind.Trailed0, 46u)]
    [Arguments(654u, AttachPointKind.Trailed0, 122u)]
    [Arguments(565u, AttachPointKind.Trailed0, 28u)]
    [Arguments(604u, AttachPointKind.System, 30u)]
    public async Task OtherBindingsKeepTheirPlacement(uint modelId, AttachPointKind point, uint childId)
    {
        var motor = Motor(modelId, point, childId);
        await Assert.That(motor.TrailerSocket).IsNull();
        await Assert.That(motor.UsesRoutePose).IsFalse();
    }

    [Test]
    [Arguments(25u, 0u, true)]
    [Arguments(25u, 28u, false)]
    [Arguments(40u, 28u, false)]
    [Arguments(5u, 43u, false)]
    public async Task RailCartsWithoutAStockTrailerUseOnlyTheSingleCartRoutePose(uint motorId, uint trailerId, bool usesRoutePose)
    {
        var motor = Motor(565, AttachPointKind.Trailed0, trailerId);
        motor.Template.Id = motorId;
        if (trailerId == 0)
            motor.Template.TransferBindings.Clear();
        await Assert.That(motor.TrailerSocket).IsNull();
        await Assert.That(motor.UsesRoutePose).IsEqualTo(usesRoutePose);
    }

    [Test]
    public async Task MarianopleTram_BothBenchesUseTheCabinModelSockets()
    {
        var world = SetUpStation(modelId: 606, vehicleModelId: 20);
        var motorTemplate = Motor(606, AttachPointKind.Trailed0, 4).Template;
        motorTemplate.Id = 3;
        var start = new WorldSpawnPosition { X = 10936.2905f, Y = 12044.841f, Z = 130.25333f, ZoneId = 183 };
        var next = new WorldSpawnPosition { X = 10933.959f, Y = 12024.92f, Z = 131.47528f, ZoneId = 183 };
        motorTemplate.TransferAllPaths.Add(new TransferPaths { Id = 7, OwnerId = 3, PathName = "mari_tram_01", WaitTimeEnd = 15 });
        motorTemplate.TransferRoads.Add(new TransferRoads { Name = "mari_tram_01", Pos = [start, next] });
        ConfigureSpawn(motorTemplate, cabinModelId: 314, seatId: 5921, phaseId: 15276);
        world.TransferManager = new TransferManager();
        var motor = new TransferSpawner { ParentWorld = world, Position = start, UnitId = 3 }.Spawn(0);
        var cabin = motor.Bounded;
        await Assert.That(cabin.BondingObjId).IsEqualTo(motor.ObjId);
        await Assert.That(cabin.AttachedDoodads.Count).IsEqualTo(2);
        var passenger = new CharacterMock { Id = 10, ObjId = 100, ParentWorld = world };

        var front = cabin.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Passenger0);
        var rear = cabin.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Passenger1);
        // Compiled nodes $passenger0 and $passenger1 on transfers_floatship02.cgf.
        var frontSocket = new Vector3(0.02091537f, 5.013110f, 1.463596f);
        var rearSocket = new Vector3(0.02785782f, -2.598603f, 1.463596f);
        await CheckTramBench(front, cabin, frontSocket, -Vector3.UnitY);
        await CheckTramBench(rear, cabin, rearSocket, Vector3.UnitY);
        await BoardAndLeave(passenger, front);
        await BoardAndLeave(passenger, rear);

        var parked = cabin.Transform.World.Position;
        motor.WaitTime = DateTime.MinValue;
        motor.MoveTo(motor);
        motor.WaitTime = DateTime.MinValue;
        motor.MoveTo(motor);
        await Assert.That(Vector3.Distance(parked, cabin.Transform.World.Position)).IsGreaterThan(0.01f);
        await CheckTramBench(front, cabin, frontSocket, -Vector3.UnitY);
        await CheckTramBench(rear, cabin, rearSocket, Vector3.UnitY);
        await BoardAndLeave(passenger, rear);
    }

    private static async Task CheckTramBench(Doodad bench, Transfer cabin, Vector3 local, Vector3 forward)
    {
        await Assert.That(bench.ParentObjId).IsEqualTo(cabin.ObjId);
        await Assert.That(bench.Transform.Parent).IsSameReferenceAs(cabin.Transform);
        await CheckPoint(bench.Transform.Local.Position, local);
        await CheckPoint(bench.Transform.World.Position, cabin.Transform.TransformPoint(local));
        await CheckPoint(Vector3.Transform(Vector3.UnitY, bench.Transform.Local.ToQuaternion()), forward);
    }

    private static Transfer Motor(uint modelId, AttachPointKind point, uint childId)
    {
        var template = new TransferTemplate { Id = 6, ModelId = modelId, WaitTime = 20, Cyclic = true, PathSmoothing = 8 };
        template.TransferBindings.Add(new TransferBindings { AttachPointId = point, TransferId = childId });
        return new Transfer { Template = template, TemplateId = template.Id, ModelId = modelId, ObjId = 1 };
    }

    private WorldInstance SetUpStation(int cells = 16, uint modelId = 654, uint vehicleModelId = 22)
    {
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, GeoDataMode = true };
        var world = CreateStationWorld(cells);
        var worlds = new WorldManager(null, null, null, null, null);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [0] = world });
        SetSingleton(worlds);
        SetSingleton(new PermissionManager(Mock.Of<IAccountManager>().Object));
        var zones = new ZoneManager(worlds, null);
        SetField(zones, "_zones", new Dictionary<uint, AAEmu.Game.Models.Game.World.Zones.Zone>());
        SetSingleton(zones);
        var models = new ModelManager();
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
        {
            ["VehicleModel"] = new() { [vehicleModelId] = new VehicleModel
            {
                Id = vehicleModelId, Velocity = 8, AngVel = 1, WheeledVehicleMass = 1000,
                WheeledVehicleMaxGear = 2, WheeledVehicleGearSpeedRatio2 = 1
            } }
        });
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>
            { [modelId] = new() { Id = modelId, SubId = vehicleModelId, SubType = "VehicleModel" } });
        SetSingleton(models);
        return world;
    }

    private static WorldInstance CreateStationWorld(int cells)
    {
        var regions = cells * WorldManager.SECTORS_PER_CELL;
        var template = new WorldTemplate
        {
            CellX = cells, CellY = cells, Cells = new WorldCell[cells, cells],
            ZoneKeyByRegions = new uint[regions, regions]
        };
        // The old cabin sampled this navigation height at its JSON spawn before the motor took the route.
        var oldCabin = new Vector3(12609.46f, 15349.916f, 157.85512f);
        template.ZoneKeyByRegions[(int)oldCabin.X / WorldManager.REGION_SIZE, (int)oldCabin.Y / WorldManager.REGION_SIZE] = 195;
        var bai = new BaseBaiLoader(template);
        var navigation = new NetMissionReader(Stream.Null, 195);
        navigation.NodeDescriptorList.TryAdd(1, new NodeDescriptor(navigation)
            { Id = 1, Pos = oldCabin, NavigationType = BaiNavigationType.Triangular });
        bai.NetMissionReaders.Add(navigation);
        template.ZoneBaiLoader.Add(195, bai);
        template.GeoData = new AiGeoDataManager(template);
        return new WorldInstance(template, 0, true, 0) { Regions = new Region[regions, regions] };
    }

    private TransferTemplate ConfigureSpawn(TransferTemplate motor, uint cabinModelId = 653, uint seatId = 5890, uint phaseId = 15181,
        (uint Id, uint Phase)? rear = null)
    {
        var (rearId, rearPhase) = rear ?? (seatId, phaseId);
        var cabin = new TransferTemplate { Id = motor.TransferBindings[0].TransferId, ModelId = cabinModelId };
        cabin.TransferBindingDoodads.Add(new TransferBindingDoodads { DoodadId = seatId, AttachPointId = AttachPointKind.Passenger0 });
        cabin.TransferBindingDoodads.Add(new TransferBindingDoodads { DoodadId = rearId, AttachPointId = AttachPointKind.Passenger1 });
        var data = new TransferGameData();
        SetField(data, "_templates", new Dictionary<uint, TransferTemplate> { [motor.Id] = motor, [cabin.Id] = cabin });
        SetSingleton(data);
        var buffs = new BuffGameData();
        SetEmptyDictionaries(buffs);
        SetSingleton(buffs);
        var factions = new FactionManager(null);
        SetField(factions, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>());
        SetSingleton(factions);
        var formulas = new FormulaManager();
        var constant = new UnitFormula();
        typeof(Formula).GetProperty("Expression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(constant, (Func<Dictionary<string, double>, double>)(_ => 100));
        SetField(formulas, "_unitFormulas", new Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>>
            { [FormulaOwnerType.Transfer] = Enum.GetValues<UnitFormulaKind>().ToDictionary(kind => kind, _ => constant) });
        SetSingleton(formulas);
        var objects = new ObjectIdManager();
        objects.Initialize();
        SetIdManager(objects);
        var ids = new TlIdManager();
        ids.Initialize();
        SetIdManager(ids);
        var skills = new SkillManager(null, null);
        SetEmptyDictionaries(skills);
        var untouchable = (uint)BuffConstants.Untouchable;
        SetField(skills, "_buffs", new Dictionary<uint, BuffTemplate> { [untouchable] = new() { Id = untouchable, Kind = BuffKind.Good } });
        SetSingleton(skills);
        var doodads = new DoodadManager(objects, null, null, null, null);
        SetEmptyDictionaries(doodads);
        var doodadTemplates = new Dictionary<uint, DoodadTemplate>();
        foreach (var (id, phase) in new[] { (seatId, phaseId), (rearId, rearPhase) }.Distinct())
        {
            var doodad = new DoodadTemplate { Id = id };
            doodad.FuncGroups.Add(new DoodadFuncGroups { Id = phase, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start });
            doodadTemplates.Add(id, doodad);
        }
        SetField(doodads, "_templates", doodadTemplates);
        SetSingleton(doodads);
        return cabin;
    }

    private static void SetEmptyDictionaries<T>(T manager)
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                field.SetValue(manager, Activator.CreateInstance(field.FieldType));
    }

    private void SetIdManager<T>(T value)
    {
        var field = typeof(T).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static async Task BoardAndLeave(CharacterMock passenger, Doodad seat, uint skillId = 15857,
        AttachPointKind attachPoint = AttachPointKind.Passenger0)
    {
        // Both bench types use stock skill 15857. The safari chair uses 23464 with attachment 193.
        var action = new DoodadFuncAttachment { AttachPointId = attachPoint, Space = 2, BondKindId = BondKind.BondChairDouble };
        passenger.Transform.Local.SetPosition(seat.Transform.World.Position);
        action.Use(passenger, seat, skillId);
        await Assert.That(passenger.Bonding.GetOwner()).IsSameReferenceAs(seat);
        await Assert.That(passenger.Transform.Parent).IsSameReferenceAs(seat.Transform);
        await CheckPoint(passenger.Transform.World.Position, seat.Transform.World.Position);
        var request = new PacketStream();
        request.WriteBc(passenger.ObjId);
        request.WriteBc(seat.ObjId);
        var leave = new CSUnbondDoodadPacket { Connection = new GameConnection(null) { ActiveChar = passenger } };
        leave.Read(request);
        await Assert.That(passenger.Bonding).IsNull();
        await Assert.That(passenger.Transform.Parent).IsNull();
        await CheckPoint(passenger.Transform.World.Position, seat.Transform.World.Position);
    }

    private static async Task CheckSeats(Transfer motor, Transfer cabin, Doodad first, Doodad second)
    {
        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, motor.Rot));
        var right = Vector3.Transform(Vector3.UnitX, motor.Rot);
        var expected = motor.Transform.World.Position - forward * 9.24417f + Vector3.UnitZ * 1.2507583f;
        await CheckPoint(cabin.Transform.World.Position, expected);
        await Assert.That(cabin.Rot).IsEqualTo(motor.Rot);
        await Assert.That(cabin.BondingObjId).IsEqualTo(motor.ObjId);
        await Assert.That(first.Transform.Parent).IsSameReferenceAs(cabin.Transform);
        await Assert.That(second.Transform.Parent).IsSameReferenceAs(cabin.Transform);
        var seatBase = expected + right * 0.00537476f + Vector3.UnitZ * 1.36648f;
        await CheckPoint(first.Transform.World.Position, seatBase + forward * 5.7852f);
        await CheckPoint(second.Transform.World.Position, seatBase + forward * 1.63614f);
    }

    private static async Task CheckPoint(Vector3 value, Vector3 expected)
        => await Assert.That(Vector3.Distance(value, expected)).IsLessThan(0.003f);

    private static void SetField<T>(T owner, string name, object value)
        => typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private void SetSingleton<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }
}
