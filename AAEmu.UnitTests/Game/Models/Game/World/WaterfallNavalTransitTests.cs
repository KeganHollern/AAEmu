using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics;

using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public class WaterfallNavalTransitTests
{
    private const ushort HeightUnitsPerMeter = 8;

    [Test]
    public async Task AddFromCellData_WaterfallBrush_IndexesWorldBoundsWithoutCreatingFlatWater()
    {
        var template = new WorldTemplate { Name = "main_world", CellX = 3, CellY = 4 };
        var cell = new WorldCell(1, 2, template)
        {
            LoadedObjectDat = new ObjectsFile("test")
            {
                AssetPathsList =
                [
                    new AssetPath { Name = "objects/natural/waterfall/waterfall_size_15.cgf" },
                    new AssetPath { Name = "objects/rocks/ordinary_rock.cgf" }
                ],
                PrefabsList =
                [
                    new ObjectDataType1Brush
                    {
                        PathId = 0,
                        StartPos = new Vector3(10f, 20f, 90f),
                        EndPos = new Vector3(30f, 40f, 112f)
                    },
                    new ObjectDataType1Brush
                    {
                        PathId = 1,
                        StartPos = new Vector3(50f, 50f, 90f),
                        EndPos = new Vector3(60f, 60f, 112f)
                    }
                ]
            }
        };
        var water = new WaterBodies { OceanLevel = 100f };

        water.AddFromCellData(cell);

        var waterfall = water.GetWaterfallsSnapshot().Single();
        await Assert.That(water.Areas).IsEmpty();
        await Assert.That(waterfall.Min.X).IsEqualTo(WorldManager.CELL_SIZE + 10f).Within(0.001f);
        await Assert.That(waterfall.Min.Y).IsEqualTo(WorldManager.CELL_SIZE * 2f + 20f).Within(0.001f);
        await Assert.That(waterfall.Min.Z).IsEqualTo(90f).Within(0.001f);
        await Assert.That(waterfall.Max.Z).IsEqualTo(112f).Within(0.001f);

        var hits = water.GetWaterfallsIntersecting(
            waterfall.Min.X - 1f, waterfall.Min.Y - 1f, 110f,
            waterfall.Min.X + 1f, waterfall.Min.Y + 1f, 113f);
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ClearIngestedAreas_RemovesWaterfallTransitionIndex()
    {
        var water = CreateWaterWithWaterfall();
        await Assert.That(water.GetWaterfallsSnapshot().Count).IsEqualTo(1);

        water.ClearIngestedAreas();

        await Assert.That(water.GetWaterfallsSnapshot()).IsEmpty();
        await Assert.That(water.GetWaterfallsIntersecting(-100f, -100f, -100f, 100f, 100f, 200f)).IsEmpty();
    }

    [Test]
    public async Task ExactDewstoneDrop_ComputesReplicableClearanceVelocity()
    {
        const float measuredDrop = 111.534f - 100f;
        const float clearanceDistance = 20f;

        var fallTime = ShipWaterfallInteraction.ComputeFallTime(
            measuredDrop, ShipWaterfallInteraction.LaunchUpwardMetersPerSecond);
        var launchSpeed = ShipWaterfallInteraction.ComputeRequiredHorizontalSpeed(
            clearanceDistance, measuredDrop, ShipWaterfallInteraction.LaunchUpwardMetersPerSecond);

        await Assert.That(fallTime).IsGreaterThan(1.5f);
        await Assert.That(fallTime).IsLessThan(1.8f);
        await Assert.That(launchSpeed).IsGreaterThan(12f);
        await Assert.That(launchSpeed).IsLessThan(ShipWaterfallInteraction.MaximumReplicableHorizontalSpeed);
    }

    [Test]
    public async Task ComposeLaunchVelocity_FollowsDownstreamAndAddsLipClearance()
    {
        var downstream = Vector2.Normalize(new Vector2(1f, -1f));

        var velocity = ShipWaterfallInteraction.ComposeLaunchVelocity(downstream, 14f, -6f);

        await Assert.That(velocity.X).IsGreaterThan(9f);
        await Assert.That(velocity.Z).IsLessThan(-9f);
        await Assert.That(velocity.Y).IsEqualTo(ShipWaterfallInteraction.LaunchUpwardMetersPerSecond)
            .Within(0.001f);
    }

    [Test]
    public async Task LowerWaterCapture_WaitsForDownstreamDescent()
    {
        var beforeEdge = ShipWaterfallInteraction.ShouldCaptureLowerWater(
            100.2f, -8f, receivingSurface: 100f, waterlineCenterOffset: 0f, reachedLowerPool: false);
        var stillRising = ShipWaterfallInteraction.ShouldCaptureLowerWater(
            100.2f, 1f, receivingSurface: 100f, waterlineCenterOffset: 0f, reachedLowerPool: true);
        var descendingInPool = ShipWaterfallInteraction.ShouldCaptureLowerWater(
            100.2f, -8f, receivingSurface: 100f, waterlineCenterOffset: 0f, reachedLowerPool: true);

        await Assert.That(beforeEdge).IsFalse();
        await Assert.That(stillRising).IsFalse();
        await Assert.That(descendingInPool).IsTrue();
    }

    [Test]
    [Arguments(null, false)]
    [Arguments(0f, true)]
    [Arguments(99f, true)]
    [Arguments(99.75f, true)]
    [Arguments(99.875f, false)]
    [Arguments(100f, false)]
    public async Task WaterfallUpdate_StartsOnlyWithEnoughMeasuredWater(float? floor, bool starts)
    {
        using var physics = new Jitter2.World();
        var ship = ShipAtWaterfall(physics, WorldManager.CELL_SIZE, floor);
        var update = new ShipWaterfallInteraction();

        await Assert.That(update.Update(ship, TimeSpan.FromMilliseconds(16))).IsEqualTo(starts);
        await Assert.That(ship.WaterfallTransitActive).IsEqualTo(starts);
        await Assert.That(ship.RigidBody.Velocity.Y > 0).IsEqualTo(starts);
    }

    [Test]
    [Arguments(null, false)]
    [Arguments(0f, true)]
    [Arguments(99f, true)]
    [Arguments(99.75f, true)]
    [Arguments(99.875f, false)]
    [Arguments(100f, false)]
    public async Task WaterfallUpdate_LandsOnMeasuredWaterOrRecovers(float? arrivalFloor, bool captures)
    {
        using var physics = new Jitter2.World();
        var ship = ShipAtWaterfall(physics, WorldManager.CELL_SIZE - 12, arrivalFloor);
        var update = new ShipWaterfallInteraction();
        var elapsed = TimeSpan.FromMilliseconds(16);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();

        ship.RigidBody.Position = new JVector(WorldManager.CELL_SIZE + 4, 100, 10);
        ship.RigidBody.Velocity = new JVector(5, -3, 0);
        await Assert.That(update.Update(ship, elapsed)).IsEqualTo(!captures);
        if (!captures)
        {
            // The existing lower-depth guard returns the hull to safe water if the flight never finds a pool.
            ship.RigidBody.Position = ship.RigidBody.Position with { Y = 80 };
            await Assert.That(update.Update(ship, elapsed)).IsFalse();
            await Assert.That(ship.RigidBody.Position.X).IsLessThan(WorldManager.CELL_SIZE - 12f);
        }
        await Assert.That(ship.WaterfallTransitActive).IsFalse();
        await Assert.That(ship.CachedWaterSurface).IsEqualTo(captures ? 100f : 112f);
        if (captures)
            await Assert.That(ship.CachedFloorLevel).IsEqualTo(arrivalFloor.Value);
    }

    [Test]
    public async Task WaterfallUpdate_ClearsShallowOverhangBeforeCapture()
    {
        using var physics = new Jitter2.World();
        var ship = ShipAtWaterfall(physics, WorldManager.CELL_SIZE - 12, 100f);
        // At x=1028 the floor is at the waterline. At x=1034 the floor drops to 99 m.
        var floor = ship.ParentWorld.Template.Cells[1, 0].HeightMap;
        floor[5, 5] = 99 * HeightUnitsPerMeter;
        var update = new ShipWaterfallInteraction();
        var elapsed = TimeSpan.FromMilliseconds(16);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();
        ship.RigidBody.Position = new JVector(WorldManager.CELL_SIZE + 4, 100, 10);
        ship.RigidBody.Velocity = new JVector(5, -3, 0);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();

        ship.RigidBody.Position = new JVector(WorldManager.CELL_SIZE + 10, 99.7f, 10);
        await Assert.That(update.Update(ship, elapsed)).IsFalse();
        await Assert.That(ship.WaterfallTransitActive).IsFalse();
        await Assert.That(ship.CachedFloorLevel).IsEqualTo(99f);
        await Assert.That(ship.CachedWaterSurface).IsEqualTo(100f);
    }

    [Test]
    public async Task WaterfallUpdate_RemainsInFlightPastTheMeasuredPoolOnDryGround()
    {
        using var physics = new Jitter2.World();
        const int lip = WorldManager.CELL_SIZE - 12;
        var ship = ShipAtWaterfall(physics, lip, 99f);
        var water = ship.ParentWorld.Water;
        water.OceanLevel = 90;
        water.AddSquareArea("receiving pool", new(lip + 6, 10, 100), 12, 12);
        await Assert.That(water.GetWaterSurface(new(lip + 4, 10, 90), out _)).IsEqualTo(100f);
        await Assert.That(water.GetWaterSurface(new(lip + 16, 10, 100), out _)).IsEqualTo(90f);
        var update = new ShipWaterfallInteraction();
        var elapsed = TimeSpan.FromMilliseconds(16);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();

        // The projected pool has enough water. At the actual arrival XY the bed is above the ocean.
        ship.RigidBody.Position = new JVector(WorldManager.CELL_SIZE + 4, 100, 10);
        ship.RigidBody.Velocity = new JVector(5, -3, 0);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();
        await Assert.That(ship.WaterfallTransitActive).IsTrue();
        await Assert.That(ship.CachedWaterSurface).IsEqualTo(112f);
    }

    [Test]
    [Arguments(100f, 0f)]
    [Arguments(103f, -2f)]
    [Arguments(108f, 2f)]
    public async Task WaterfallUpdate_CapturesAtActualWaterlineWithLocalFlow(float surface, float flow)
    {
        using var physics = new Jitter2.World();
        const int lip = WorldManager.CELL_SIZE - 12;
        var ship = ShipAtWaterfall(physics, lip, 88f);
        var water = ship.ParentWorld.Water;
        AddFlowingPool(water, "projected pool", lip + 6, 105, 3);
        if (flow != 0)
            AddFlowingPool(water, "arrival pool", lip + 16, surface, flow);
        var update = new ShipWaterfallInteraction();
        var elapsed = TimeSpan.FromMilliseconds(16);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();
        ship.RigidBody.Velocity = new JVector(5, -3, 0);

        if (surface < 105f)
        {
            ship.RigidBody.Position = new JVector(lip + 16, 105, 10);
            await Assert.That(update.Update(ship, elapsed)).IsTrue();
        }
        ship.RigidBody.Position = new JVector(lip + 16, surface - 0.5f, 10);
        await Assert.That(update.Update(ship, elapsed)).IsFalse();
        await Assert.That(ship.WaterfallTransitActive).IsFalse();
        await Assert.That(ship.CachedWaterSurface).IsEqualTo(surface);
        await Assert.That(ship.CachedWaterFlow).IsEqualTo(new Vector3(flow, 0, 0));
        await Assert.That(ship.RigidBody.Position.Y).IsEqualTo(surface - 0.35f).Within(0.001f);
        await Assert.That(ship.RigidBody.Velocity.X).IsEqualTo(flow + (5 - flow) * 0.65f).Within(0.001f);
    }

    [Test]
    [Arguments(112f, true)]
    [Arguments(84f, false)]
    public async Task WaterfallUpdate_RefusesLocalSurfaceOutsideTheOriginalDrop(float surface, bool waitForTimeout)
    {
        using var physics = new Jitter2.World();
        const int lip = WorldManager.CELL_SIZE - 12;
        var ship = ShipAtWaterfall(physics, lip, 60f);
        var water = ship.ParentWorld.Water;
        AddFlowingPool(water, "projected pool", lip + 6, 105, 3);
        AddFlowingPool(water, "arrival pool", lip + 16, surface, -2);
        var update = new ShipWaterfallInteraction();
        var elapsed = TimeSpan.FromMilliseconds(16);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();
        ship.RigidBody.Position = new JVector(lip + 16, 105, 10);
        ship.RigidBody.Velocity = new JVector(5, -3, 0);
        await Assert.That(update.Update(ship, elapsed)).IsTrue();
        if (waitForTimeout)
        {
            // The stationary hull stays above the depth guard until the original flight deadline.
            for (var tick = 0; tick < 15; tick++)
                await Assert.That(update.Update(ship, TimeSpan.FromSeconds(0.25))).IsTrue();
            for (var tick = 0; tick < 24 && ship.WaterfallTransitActive; tick++)
                update.Update(ship, TimeSpan.FromSeconds(0.25));
        }
        else
        {
            ship.RigidBody.Position = ship.RigidBody.Position with { Y = 80 };
            await Assert.That(update.Update(ship, elapsed)).IsFalse();
        }
        await Assert.That(ship.WaterfallTransitActive).IsFalse();
        await Assert.That(ship.CachedWaterSurface).IsEqualTo(112f);
        await Assert.That(ship.RigidBody.Position.X).IsLessThan(lip);
    }

    private static void AddFlowingPool(WaterBodies water, string name, float x, float surface, float flow)
    {
        var pool = water.AddSquareArea(name, new(x, 10, surface), 12, 32);
        pool.FlowAxis = Vector2.UnitX;
        pool.FlowSpeedAbs = MathF.Abs(flow);
        pool.FlowSpeedSigned = flow;
    }

    [Test]
    [Arguments("Waterfall_C12-14_62", 12529.9375f, 14393.265f, 12521.9297f, 14370.0205f, 96.9378f)]
    [Arguments("Waterfall_C12-14_352", 12551.777f, 14384.766f, 12543.4834f, 14360.6807f, 95.4505f)]
    public async Task DewstoneClipperLandings_HaveMeasuredOceanBelowTheirLips(
        string brush, float sourceX, float sourceY, float landX, float landY, float bed)
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 Dewstone waterfall check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            const string cellPath = "game/worlds/main_world/cells/012_014/client";
            using var objectsStream = source.GetFileStream(cellPath + "/object.dat");
            var objects = new ObjectsFile(cellPath);
            await Assert.That(objects.ReadFile(objectsStream)).IsTrue();
            var water = new WaterBodies { OceanLevel = 100f };
            water.AddFromCellData(new WorldCell(12, 14, new WorldTemplate()) { LoadedObjectDat = objects });
            await Assert.That(water.GetWaterfallsSnapshot().Any(fall => fall.Name == brush)).IsTrue();
            await Assert.That(water.GetWaterSurface(new Vector3(sourceX, sourceY, 111.534f), out _))
                .IsEqualTo(111.534f).Within(0.01f);
            await Assert.That(water.GetWaterSurface(new Vector3(landX, landY, 100f), out _)).IsEqualTo(100f);
            using var heightStream = source.GetFileStream(cellPath + "/terrain/heightmap.dat");
            var terrain = CryTerrainGrid.Read(heightStream, new Vector2(12, 14) * WorldManager.CELL_SIZE);
            await Assert.That(terrain.SampleHeight(landX, landY)).IsEqualTo(bed).Within(0.001f);
        }
        finally
        {
            source.Close();
        }
    }

    private static Slave ShipAtWaterfall(Jitter2.World physics, int lipX, float? nextCellFloor)
    {
        var template = WorldTemplateTests.CreateWorldTemplate(2, 1);
        template.HeightMaxCoefficient = HeightUnitsPerMeter;
        WorldTemplateTests.SetLoadedCell(template, 0, 0, WorldTemplateTests.CreateHeightMap(88 * HeightUnitsPerMeter));
        if (nextCellFloor.HasValue)
        {
            var rawFloor = (ushort)(nextCellFloor.Value * HeightUnitsPerMeter);
            WorldTemplateTests.SetLoadedCell(template, 1, 0, WorldTemplateTests.CreateHeightMap(rawFloor));
        }
        var world = new WorldInstance(template, 0, true, 0) { Water = CreateWaterWithWaterfall(lipX) };
        world.Water.OceanLevel = 100;
        var source = new WaterBodyArea("source", WaterBodyAreaType.Polygon)
        {
            Points = [new(lipX - 20, 0, 112), new(lipX, 0, 112), new(lipX, 20, 112), new(lipX - 20, 20, 112)],
            Depth = 30
        };
        source.UpdateBounds();
        world.Water.Areas.Add(source);
        world.Water.RebuildSpatialIndex();
        var controller = new ShipController(physics, new ShipModelV1
        {
            Mass = 100, MassBoxSizeX = 2, MassBoxSizeY = 6, MassBoxSizeZ = 2
        });
        controller.Build(new JVector(lipX - 4, 112f, 10f), JQuaternion.Identity);
        controller.Hull.Velocity = new JVector(2, 0, 0);
        var ship = new Slave
        {
            RigidBody = controller.Hull, ShipController = controller
        };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ship, world);
        ship.Transform.Local.SetPosition(lipX - 4, 10, 112);
        ship.CreateWaterAndLandSurfaceCache();
        return ship;
    }

    private static WaterBodies CreateWaterWithWaterfall(int lipX = 20)
    {
        var template = new WorldTemplate { Name = "main_world", CellX = 1, CellY = 1 };
        var cell = new WorldCell(0, 0, template)
        {
            LoadedObjectDat = new ObjectsFile("test")
            {
                AssetPathsList = [new AssetPath { Name = "waterfall_size_15.cgf" }],
                PrefabsList =
                [
                    new ObjectDataType1Brush
                    {
                        PathId = 0,
                        StartPos = new Vector3(lipX - 20, 0f, 90f),
                        EndPos = new Vector3(lipX, 20f, 112f)
                    }
                ]
            }
        };
        var water = new WaterBodies();
        water.AddFromCellData(cell);
        return water;
    }
}
