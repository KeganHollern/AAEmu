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
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class CarriageBoardingTests
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
    public async Task CarriageWithoutRoute_UsesThePlacedMotorPoseIncludingRollAndPitch()
    {
        var motor = Motor(654, AttachPointKind.Trailed0, 46);
        var spawn = new WorldSpawnPosition { X = 150, Y = 160, Z = 0, Roll = 0.2f, Pitch = -0.1f, Yaw = 0.8f };
        var cabin = new Transfer { ModelId = 653 };
        motor.InitializeCarriageMotor(spawn);
        motor.PlaceCarriageCabin(cabin, false);
        var rotation = motor.Transform.Local.ToQuaternion();
        var socket = Vector3.Transform(new Vector3(0, -9.24417f, 1.2507583f), rotation);
        await CheckPoint(cabin.Transform.World.Position, new Vector3(150, 160, 0) + socket);
        await Assert.That(cabin.Rot).IsEqualTo(rotation);
    }

    [Test]
    [Arguments(654u, AttachPointKind.Passenger0, 46u)]
    [Arguments(1463u, AttachPointKind.Trailed0, 122u)]
    [Arguments(565u, AttachPointKind.Trailed0, 28u)]
    [Arguments(604u, AttachPointKind.System, 30u)]
    public async Task OtherBindingsKeepTheirPlacement(uint modelId, AttachPointKind point, uint childId)
    {
        await Assert.That(Motor(modelId, point, childId).UsesCarriageSocket).IsFalse();
    }

    [Test]
    public async Task MarianopleTram_BothBenchesUseTheCabinModelSockets()
    {
        var world = SetUpStation();
        var motorTemplate = Motor(606, AttachPointKind.Trailed0, 4).Template;
        motorTemplate.Id = 3;
        var start = new WorldSpawnPosition { X = 10936.2905f, Y = 12044.841f, Z = 130.25333f, ZoneId = 183 };
        var next = new WorldSpawnPosition { X = 10933.959f, Y = 12024.92f, Z = 131.47528f, ZoneId = 183 };
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
        await CheckTramBench(front, cabin, new Vector3(0.02091537f, 5.013110f, 1.463596f), -Vector3.UnitY);
        await CheckTramBench(rear, cabin, new Vector3(0.02785782f, -2.598603f, 1.463596f), Vector3.UnitY);
        await BoardAndLeave(passenger, front);
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

    private WorldInstance SetUpStation()
    {
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, GeoDataMode = true };
        var world = CreateStationWorld();
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
            ["VehicleModel"] = new() { [22] = new VehicleModel
            {
                Id = 22, Velocity = 8, AngVel = 1, WheeledVehicleMass = 1000,
                WheeledVehicleMaxGear = 2, WheeledVehicleGearSpeedRatio2 = 1
            } }
        });
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>
            { [654] = new() { Id = 654, SubId = 22, SubType = "VehicleModel" } });
        SetSingleton(models);
        return world;
    }

    private static WorldInstance CreateStationWorld()
    {
        const int cells = 16;
        const int regions = cells * WorldManager.SECTORS_PER_CELL;
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

    private void ConfigureSpawn(TransferTemplate motor, uint cabinModelId = 653, uint seatId = 5890, uint phaseId = 15181)
    {
        var cabin = new TransferTemplate { Id = motor.TransferBindings[0].TransferId, ModelId = cabinModelId };
        cabin.TransferBindingDoodads.Add(new TransferBindingDoodads { DoodadId = seatId, AttachPointId = AttachPointKind.Passenger0 });
        cabin.TransferBindingDoodads.Add(new TransferBindingDoodads { DoodadId = seatId, AttachPointId = AttachPointKind.Passenger1 });
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
        SetField(skills, "_buffs", new Dictionary<uint, BuffTemplate> { [untouchable] = new() { Id = untouchable } });
        SetSingleton(skills);
        var doodads = new DoodadManager(objects, null, null, null, null);
        SetEmptyDictionaries(doodads);
        var seat = new DoodadTemplate { Id = seatId };
        seat.FuncGroups.Add(new DoodadFuncGroups { Id = phaseId, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start });
        SetField(doodads, "_templates", new Dictionary<uint, DoodadTemplate> { [seat.Id] = seat });
        SetSingleton(doodads);
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

    private static async Task BoardAndLeave(CharacterMock passenger, Doodad seat)
    {
        // Both stock benches use skill 15857 and the same two-place seat action.
        var action = new DoodadFuncAttachment { AttachPointId = AttachPointKind.Passenger0, Space = 2, BondKindId = BondKind.BondChairDouble };
        passenger.Transform.Local.SetPosition(seat.Transform.World.Position);
        action.Use(passenger, seat, 15857);
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
