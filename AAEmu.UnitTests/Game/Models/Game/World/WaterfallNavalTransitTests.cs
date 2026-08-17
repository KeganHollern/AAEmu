using System.Numerics;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public class WaterfallNavalTransitTests
{
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

    private static WaterBodies CreateWaterWithWaterfall()
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
                        StartPos = new Vector3(0f, 0f, 90f),
                        EndPos = new Vector3(20f, 20f, 112f)
                    }
                ]
            }
        };
        var water = new WaterBodies();
        water.AddFromCellData(cell);
        return water;
    }
}
