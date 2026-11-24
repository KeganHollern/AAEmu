using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Transfers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class CarriageBoardingTests
{
    [Test]
    public async Task PublicAirship_OnlyStockPublicRoutesUseThePrefabPose()
    {
        var airship = Motor(657, AttachPointKind.Trailed0, 46);
        airship.Template.TransferBindings.Clear();
        // All 20 template IDs for vehicle model 25 in the r208022 server compact.
        foreach (var id in new uint[] { 51, 52, 65, 110, 111, 112, 113, 114, 116, 117, 118, 119 })
        {
            airship.Template.Id = id;
            await Assert.That(airship.UsesRoutePose).IsTrue();
        }
        foreach (var id in new uint[] { 1, 39, 47, 48, 83, 84, 85, 115 })
        {
            airship.Template.Id = id;
            await Assert.That(airship.UsesRoutePose).IsFalse();
        }
        airship.Template.Id = 114;
        airship.ModelId = 116; // Unpaired Birchkeep rowboat
        await Assert.That(airship.UsesRoutePose).IsFalse();
        airship.ModelId = 657;
        airship.Template.TransferBindings.Add(new TransferBindings { TransferId = 46, AttachPointId = AttachPointKind.Trailed0 });
        await Assert.That(airship.UsesRoutePose).IsFalse();
    }

    [Test]
    [Arguments(111u)]
    [Arguments(119u)]
    public async Task PublicAirship_StationObserverAndFirstMoveFaceTheFirstTravelSegment(uint id)
    {
        var (world, template) = CreateAirship(id);
        // Route 111 faces southwest. Route 119 starts with a waypoint inside the 1.5 m arrival radius.
        var points = id == 111
            ? AddTransferLeg(template, 572, "air_hell_long_1", 245, 90, 0,
                new(10728.4072f, 9374.8196f, 188.75999f), new(10684.3547f, 9343.0369f, 202.70792f))
            : AddTransferLeg(template, 595, "air_loka_hasla_1", 138, 90, 0,
                new(25367.3768f, 9749.2046f, 774.56689f), new(25367.7612f, 9748.9351f, 774.57239f),
                new(25372.8812f, 9738.27f, 774.50543f));
        var (ship, observer, session) = await SpawnUnboundTransfer(world, template);
        var start = points[0];
        var target = points[^1];
        var direction = target.AsPositionVector() - start.AsPositionVector();
        await CheckPoint(ship.Transform.World.Position, start.AsPositionVector());
        await CheckObserverHeading(ship, observer, session, direction);
        await Assert.That(ship.TimeLeft).IsGreaterThan(89000u);
        var parked = ship.Transform.World.Position;
        for (var tick = 0; tick < 5 && ship.Transform.World.Position == parked; tick++)
        {
            ship.WaitTime = DateTime.MinValue;
            session.Packets.Clear();
            ship.MoveTo(ship);
        }
        await Assert.That(Vector3.Distance(parked, ship.Transform.World.Position)).IsGreaterThan(0.1f);
        await CheckMovementAndObserver(ship, observer, session, direction);
    }

    [Test]
    public async Task PublicAirship_ObserversSeeItsHeadingOnArrivalAtTheDockAndDeparture()
    {
        var (world, template) = CreateAirship(114);
        // The final two points of leg 6 and the first two of leg 7 meet at the real Karkasse dock.
        var arrival = AddTransferLeg(template, 629, "air_mari_carcass_6", 145, 0, 90,
            new(10447.2886f, 17331.3718f, 257.89609f), new(10522.9448f, 17317.6331f, 257.79001f));
        var departure = AddTransferLeg(template, 630, "air_mari_carcass_7", 145, 0, 0,
            new(10522.9451f, 17317.6331f, 257.79001f), new(10572.9763f, 17308.4037f, 257.47656f));
        var (airship, observer, session) = await SpawnUnboundTransfer(world, template);
        var approach = arrival[0];
        var stop = arrival[1];
        await CheckPoint(airship.Transform.World.Position, approach.AsPositionVector());
        var inbound = stop.AsPositionVector() - approach.AsPositionVector();
        await CheckObserverHeading(airship, observer, session, inbound);

        // Let the initial route enter its first nonzero segment, then walk the real final approach.
        var approachSteps = 0;
        for (var ticks = 0; ticks < 120 && airship.Steering == 0; ticks++)
        {
            airship.WaitTime = DateTime.MinValue;
            session.Packets.Clear();
            var before = airship.Transform.World.Position;
            airship.MoveTo(airship);
            var travel = airship.Transform.World.Position - before;
            if (travel.LengthSquared() < 0.0001f)
                continue;
            await CheckMovementAndObserver(airship, observer, session, inbound);
            approachSteps++;
        }
        await Assert.That(approachSteps).IsGreaterThan(70);
        await Assert.That(airship.Steering).IsEqualTo(1);
        await Assert.That(airship.TimeLeft).IsGreaterThan(89000u);
        var parked = airship.Transform.World.Position;
        await Assert.That(Vector3.Distance(parked, stop.AsPositionVector())).IsLessThan(airship.RangeToCheckPoint);
        session.Packets.Clear();
        airship.MoveTo(airship);
        await Assert.That(session.Packets).IsEmpty();
        await CheckPoint(airship.Transform.World.Position, parked);
        await CheckObserverHeading(airship, observer, session, inbound);

        airship.WaitTime = DateTime.MinValue;
        for (var ticks = 0; ticks < 3 && airship.Transform.World.Position == parked; ticks++)
        {
            session.Packets.Clear();
            airship.MoveTo(airship);
        }
        var outbound = departure[1].AsPositionVector() - parked;
        await Assert.That(Vector3.Dot(airship.Transform.World.Position - parked, outbound)).IsGreaterThan(0f);
        await CheckMovementAndObserver(airship, observer, session, outbound);
    }

    private (WorldInstance World, TransferTemplate Template) CreateAirship(uint id)
    {
        var world = SetUpStation(cells: 26, modelId: 657, vehicleModelId: 25);
        ModelManager.Instance.GetVehicleModels(657).Velocity = 9.6f;
        var template = new TransferTemplate { Id = id, ModelId = 657, Name = "Airship", WaitTime = 90, Cyclic = true, PathSmoothing = 15 };
        ConfigureSpawn(template);
        SetSingleton(new NameManager());
        world.TransferManager = new TransferManager();
        return (world, template);
    }

    private static List<WorldSpawnPosition> AddTransferLeg(TransferTemplate template, uint pathId, string name, uint zone,
        int startWait, int endWait, params Vector3[] points)
    {
        var route = points.Select(p => new WorldSpawnPosition { X = p.X, Y = p.Y, Z = p.Z, ZoneId = zone }).ToList();
        template.TransferAllPaths.Add(new TransferPaths
            { Id = pathId, OwnerId = template.Id, PathName = name, WaitTimeStart = startWait, WaitTimeEnd = endWait });
        template.TransferRoads.Add(new TransferRoads { Name = name, Pos = route });
        return route;
    }

    private static async Task<(Transfer Ship, CharacterMock Observer, TransferVisibilityTests.RecordingSession Session)>
        SpawnUnboundTransfer(WorldInstance world, TransferTemplate template)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == template.Id);
        spawner.ParentWorld = world;
        var ship = spawner.Spawn(0);
        await Assert.That(ship.Bounded).IsNull();
        var session = new TransferVisibilityTests.RecordingSession();
        var observer = new CharacterMock { ObjId = 400, ParentWorld = world, Connection = new GameConnection(session) };
        observer.Transform.Local.SetPosition(ship.Transform.World.Position);
        world.GetRegionByPos(ship.Transform.World.Position).AddObject(observer);
        return (ship, observer, session);
    }
}
