using System.Reflection;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Physics.HeightMaps;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.World;

[NotInParallel]
public sealed class TerrainPhysicsTests
{
    private WorldConfig _previousSettings;

    [Before(Test)]
    public void SetUp()
    {
        _previousSettings = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World ??= new();
    }

    [After(Test)]
    public void TearDown() => AppConfiguration.Instance.World = _previousSettings;

    private sealed class Scene : IDisposable
    {
        public WorldTemplate Template { get; }
        public Jitter2.World Physics { get; private set; }

        public Scene(int width, int height)
        {
            Template = new WorldTemplate { CellX = width, CellY = height, HeightMaxCoefficient = 1,
                Cells = new WorldCell[width, height] };
            for (var x = 0; x < width; x++)
            for (var z = 0; z < height; z++)
                Template.Cells[x, z] = new WorldCell(x, z, Template);
        }

        public void Publish(int x, int z, Func<int, int, ushort> sample)
        {
            const int size = WorldManager.CELL_HMAP_RESOLUTION;
            var cell = Template.Cells[x, z];
            if (sample != null)
            {
                var values = new ushort[size, size];
                for (var i = 0; i < size; i++)
                for (var j = 0; j < size; j++)
                    values[i, j] = sample(i, j);
                typeof(WorldCell).GetProperty("HeightMap", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cell, values);
            }
            typeof(WorldCell).GetProperty("Loaded")!.SetValue(cell, true);
        }

        public void Start()
        {
            var manager = new PhysicsManager { SimulationWorld = new WorldInstance(Template, 1, true, 1) };
            manager.Initialize();
            manager.InitializeTerrain();
            Physics = manager._physWorld;
        }

        public float Drop(float x, float height, float z, Slave ship = null)
        {
            var body = Physics.CreateRigidBody();
            body.Tag = ship;
            body.AddShape(new BoxShape(.8f, .8f, .8f));
            body.Position = new JVector(x, height + 1.5f, z);
            for (var i = 0; i < 120; i++)
                Physics.Step(1f / 60, false);
            var result = body.Position.Y;
            Physics.Remove(body);
            return result;
        }

        public bool GroundRay(float x, float z, out float height)
        {
            var hit = Physics.DynamicTree.RayCast(new JVector(x, 60, z), new JVector(0, -1, 0), null, null,
                out _, out _, out var distance);
            height = 60 - distance;
            return hit;
        }

        public void Dispose() => Physics?.Dispose();
    }

    [Test]
    [Arguments(false, 0, 20.3f, 20.3f)]
    [Arguments(false, 30, 800.3f, 900.3f)]
    [Arguments(true, 30, 800.3f, 900.3f)]
    [Arguments(true, 20, 1020.3f, 2044.3f)]
    public async Task FallingBoxRestsOnMeasuredFloor(bool late, int floor, float x, float z)
    {
        using var scene = new Scene(1, 2);
        var cellZ = (int)z / WorldManager.CELL_SIZE;
        if (!late)
            scene.Publish(0, cellZ, (_, _) => (ushort)floor);
        scene.Start();
        if (late)
        {
            await Assert.That(scene.GroundRay(x, z, out _)).IsFalse();
            scene.Publish(0, cellZ, (_, _) => (ushort)floor);
        }
        await Assert.That(scene.GroundRay(x, z, out var ground)).IsTrue();
        await Assert.That(Math.Abs(ground - floor)).IsLessThan(.002f);
        await Assert.That(Math.Abs(scene.Drop(x, floor, z) - floor - .39f)).IsLessThan(.1f);
    }

    [Test]
    public async Task WaterfallTransitSkipsTheOverheadTerrainUntilNormalContactsResume()
    {
        using var scene = new Scene(1, 1);
        scene.Publish(0, 0, (_, _) => 30);
        scene.Start();
        var ship = new Slave { WaterfallTransitActive = true };
        await Assert.That(scene.Drop(800.3f, 30, 900.3f, ship)).IsLessThan(25);
        ship.WaterfallTransitActive = false;
        await Assert.That(Math.Abs(scene.Drop(800.3f, 30, 900.3f, ship) - 30.39f)).IsLessThan(.1f);
    }

    [Test]
    public async Task ABoxCanRestOnAnotherBodyAboveAnAbsentFloor()
    {
        using var scene = new Scene(1, 1);
        scene.Start();
        var platform = scene.Physics.CreateRigidBody();
        platform.AddShape(new BoxShape(4, 1, 4));
        platform.Position = new JVector(20, 15.5f, 20);
        platform.MotionType = MotionType.Static;
        await Assert.That(Math.Abs(scene.Drop(20.3f, 16f, 20.3f) - 16.39f)).IsLessThan(.1f);
    }

    [Test]
    public async Task RectangularGridReservesTwoDistinctContactIdsForEverySquare()
    {
        var grid = new Heightmap(new WorldTemplate { CellX = 35, CellY = 38, HeightMaxCoefficient = 1 });
        var last = grid.FirstTriangleIndex(grid.QuadsX - 1, grid.QuadsZ - 1) + 1;
        await Assert.That(grid.FirstTriangleIndex(0, 0)).IsZero();
        await Assert.That(grid.FirstTriangleIndex(1, 0)).IsEqualTo(grid.FirstTriangleIndex(0, grid.QuadsZ - 1) + 2);
        await Assert.That(last).IsEqualTo(grid.TriangleCount - 1);
        await Assert.That((long)grid.TriangleCount).IsEqualTo(2L * (35 * 512 - 1) * (38 * 512 - 1));
    }

    [Test]
    public async Task MissingCellHasNoSurfaceAndDoesNotBridgeFromItsNeighbor()
    {
        using var scene = new Scene(2, 1);
        scene.Publish(0, 0, (_, _) => 30);
        scene.Publish(1, 0, null);
        scene.Start();
        await Assert.That(scene.GroundRay(1021.5f, 18.5f, out _)).IsTrue();
        await Assert.That(scene.GroundRay(1023f, 18.5f, out _)).IsFalse();
        await Assert.That(scene.GroundRay(1030f, 18.5f, out _)).IsFalse();
        await Assert.That(scene.Drop(1030.3f, 0, 18.5f)).IsLessThan(-5f);
    }

    [Test]
    public async Task NeighborLoadedAfterStartupCompletesSeamAndSkipsMissingFourthCorner()
    {
        using var scene = new Scene(2, 2);
        scene.Publish(0, 0, (_, _) => 10);
        scene.Start();
        await Assert.That(scene.GroundRay(1023, 1018.5f, out _)).IsFalse();
        scene.Publish(1, 0, (_, _) => 14);
        scene.Publish(0, 1, (_, _) => 18);
        await Assert.That(scene.GroundRay(1023, 1018.5f, out var seam)).IsTrue();
        await Assert.That(Math.Abs(seam - 12)).IsLessThan(.002f);
        await Assert.That(scene.GroundRay(1022.5f, 1022.5f, out var lower)).IsTrue();
        await Assert.That(Math.Abs(lower - 13)).IsLessThan(.002f);
        await Assert.That(scene.GroundRay(1023.5f, 1023.5f, out _)).IsFalse();
        scene.Publish(1, 1, (_, _) => 26);
        await Assert.That(scene.GroundRay(1023.5f, 1023.5f, out var upper)).IsTrue();
        await Assert.That(Math.Abs(upper - 21)).IsLessThan(.002f);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RayOnLastMeasuredEdgeKeepsTheAdjacentCompleteFace(bool north)
    {
        using var scene = new Scene(2, 2);
        scene.Publish(0, 0, (_, _) => 27);
        scene.Start();
        foreach (var (distance, measured) in new[] { (1021.98f, true), (1022f, true), (1022.02f, false) })
        {
            var (x, z) = north ? (20.3f, distance) : (distance, 20.3f);
            await Assert.That(scene.GroundRay(x, z, out var ground)).IsEqualTo(measured);
            if (measured)
                await Assert.That(Math.Abs(ground - 27)).IsLessThan(.002f);
        }
        await Assert.That(scene.GroundRay(1022, 1022, out var corner)).IsTrue();
        await Assert.That(Math.Abs(corner - 27)).IsLessThan(.002f);
    }

    [Test]
    public async Task ObliqueRayEntersTheGridAndAnOutsideRayMisses()
    {
        using var scene = new Scene(1, 1);
        scene.Publish(0, 0, (_, _) => 10);
        scene.Start();
        var entered = scene.Physics.DynamicTree.RayCast(new JVector(-10, 18, 20.3f), new JVector(2, -1, 0),
            null, null, out _, out var normal, out var distance);
        await Assert.That(entered).IsTrue();
        await Assert.That(Math.Abs(distance - 8)).IsLessThan(.002f);
        await Assert.That(normal.Y).IsGreaterThan(.99f);
        await Assert.That(scene.GroundRay(1022.1f, 20.3f, out _)).IsFalse();
        await Assert.That(scene.GroundRay(20.3f, -.1f, out _)).IsFalse();
    }

    [Test]
    public async Task RaysFollowBothAuthoredHalvesAndHitTheirSharedDiagonal()
    {
        using var scene = new Scene(1, 1);
        scene.Publish(0, 0, (x, z) => (ushort)((x == 11 && z == 11) ? 50 : 10));
        scene.Start();
        foreach (var (x, z, expected) in new[] { (20.5f,20.5f,10f), (21f,21f,10f), (21.5f,21.5f,30f) })
        {
            await Assert.That(scene.GroundRay(x, z, out var ground)).IsTrue();
            await Assert.That(Math.Abs(ground - expected)).IsLessThan(.002f);
            await Assert.That(scene.Template.TryGetHeight(x, z, out var surface)).IsTrue();
            await Assert.That(Math.Abs(ground - surface)).IsLessThan(.002f);
        }
    }
}
