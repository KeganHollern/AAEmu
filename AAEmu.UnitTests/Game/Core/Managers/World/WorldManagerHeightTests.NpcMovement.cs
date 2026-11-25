using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using CgfConverter.Structs;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    private const float DeckZ = 6f;
    private const float Step = 0.5f;
    private readonly List<Action> _restoreMovementServices = [];

    [Test]
    public async Task MoveTowards_ContinuousDeckAboveTerrain_KeepsFloorAcrossFiveSteps()
    {
        var npc = CreateDeckWalker();
        var world = npc.ParentWorld.Template;

        // Ezna's pier changes from a human waypoint to outdoor triangulation on the third half-metre step.
        world.GeoData.TryGetGroundSurface(new Vector3(111f, 100f, DeckZ), out var before);
        world.GeoData.TryGetGroundSurface(new Vector3(110.5f, 100f, DeckZ), out var after);
        await Assert.That(before.Source).IsEqualTo(GroundSurfaceSource.NavigationNode);
        await Assert.That(after.Source).IsEqualTo(GroundSurfaceSource.Terrain);
        await Assert.That(after.Height).IsEqualTo(0f);

        for (var i = 1; i <= 5; i++)
        {
            npc.MoveTowards(new Vector3(105f, 100f, DeckZ), Step, rangeTolerance: 0f);
            await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(112f - i * Step, 100f, DeckZ));
            await Assert.That(npc.Movements[^1].Z).IsEqualTo(DeckZ);
        }
        await Assert.That(npc.Movements.Count).IsEqualTo(5);
    }

    [Test]
    public async Task MoveTowards_LeavesDeckAtRealEdge_DescendsToTerrain()
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(110.5f, 99f, DeckZ), 1f, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 99f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_TerrainWithoutDeck_UsesTerrainHeight()
    {
        var npc = CreateDeckWalker(hasDeck: false);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(105f, 100f, DeckZ), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 100f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_GapWithinOneBrush_ReleasesPreviousFloor()
    {
        var npc = CreateDeckWalker(hasGap: true);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(105f, 100f, DeckZ), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 100f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    [Arguments(true, DeckZ)]
    [Arguments(false, 18f)]
    public async Task MoveTowards_CaveBelowTerrain_ChecksTheFloorUnderBothFeet(bool hasFloor, float expectedHeight)
    {
        var npc = CreateDeckWalker(hasDeck: hasFloor);
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, 18);

        var destination = new Vector3(109f, 100f, DeckZ);
        await Assert.That(world.GeoData.TryGetGroundSurface(destination, out var surface)).IsTrue();
        await Assert.That(surface.Source).IsEqualTo(GroundSurfaceSource.Terrain);
        await Assert.That(surface.Height).IsEqualTo(18f);

        npc.MoveTowards(destination, Step, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(111.5f, 100f, expectedHeight));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expectedHeight);
    }

    [Test]
    [Arguments(0.0043f, 0f, 1, Step, true, 18)]
    [Arguments(0.0043f, 0f, -1, Step, true, 18)]
    [Arguments(0f, 0.8f, 1, Step, false, 18)]
    [Arguments(0f, 0.8f, 1, 0.01f, false, 18)]
    [Arguments(0.1f, 0f, 1, Step, false, 18)]
    [Arguments(0f, 0.019f, 1, Step, true, 18)]
    [Arguments(0f, 0.021f, 1, Step, false, 18)]
    [Arguments(0.0043f, 0f, 1, Step, false, 0)]
    public async Task MoveTowards_GentlyInclinedCaveFace_UsesFootHeightAcrossALevelStep(
        float xSlope, float ySlope, int direction, float distance, bool expectFloor, int terrainHeight)
    {
        const float startX = 111f;
        Vector3 Point(float x, float y) => new(x, y, DeckZ + xSlope * (x - startX) + ySlope * (y - 100f));
        var mesh = new CryTriangleMesh(
            [Point(107f, 99.5f), Point(114.5f, 99.5f), Point(startX, 100.5f)], [0, 1, 2], []);
        var npc = CreateDeckWalker(customMesh: mesh);
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, (ushort)terrainHeight);
        var start = Point(startX, 100f);
        npc.Transform.Local.SetPosition(start);
        npc.MoveTowards(start + direction * Vector3.UnitX, distance, rangeTolerance: 0f);

        var expected = expectFloor ? DeckZ + direction * xSlope * distance : terrainHeight;
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(expected).Within(0.0001f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expected).Within(0.0001f);
    }

    [Test]
    [Arguments(1, Step, false)]
    [Arguments(-1, Step, false)]
    [Arguments(1, 1f, false)]
    [Arguments(-1, 1f, false)]
    [Arguments(1, Step, true)]
    [Arguments(-1, Step, true)]
    public async Task MoveTowards_UndergroundTargetOnInclinedFace_UsesItsActualFootHeight(
        int direction, float distance, bool otherLayer)
    {
        // The Sanddeep target differs by 2.22 cm across 2 m. Both feet and the target
        // lie on the same 1.11% slope. A separate floor overlaps its XY above it.
        Vector3 Point(float x, float y) => new(x, y, DeckZ + (x - 111f) * .0111f);
        var lower = new[] { Point(106f, 98f), Point(117f, 98f), Point(111f, 102f) };
        var upper = lower.Select(p => p + Vector3.UnitZ * .037f);
        var npc = CreateDeckWalker(customMesh: new CryTriangleMesh([.. lower, .. upper], [0, 1, 2, 3, 4, 5], []));
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, 18);
        var start = Point(111f - direction, 100f);
        var target = Point(111f + direction, 100f);
        if (otherLayer)
        {
            // Start on the upper floor and aim at the lower, or start on the
            // lower and aim at the upper. Both are real triangles at the same XY.
            if (direction > 0)
                start.Z += .037f;
            else
                target.Z += .037f;
        }
        npc.Transform.Local.SetPosition(start);
        npc.MoveTowards(target, distance, rangeTolerance: 0f);
        var foot = npc.Transform.Local.Position;
        var expected = otherLayer ? 18f : Point(foot.X, foot.Y).Z;
        await Assert.That(foot.Z).IsEqualTo(expected).Within(.0001f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expected).Within(.0001f);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task MoveTowards_InclinedCaveSeam_ContinuesFromEitherTriangle(bool reverseOrder, bool endAtSeam)
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + .012f * (x - 111f));
        var mesh = new CryTriangleMesh([Point(109, 98), Point(113, 98), Point(113, 102), Point(109, 102)],
            reverseOrder ? [0, 2, 3, 0, 1, 2] : [0, 1, 2, 0, 2, 3], []);
        var npc = CreateDeckWalker(customMesh: mesh);
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        SetTerrainHeight(npc.ParentWorld.Template, 18);
        var seam = Point(111, 100);
        var interior = Point(112.5f, 100);
        var (from, target) = endAtSeam ? (interior, seam) : (seam, interior);
        npc.Transform.Local.SetPosition(from);
        npc.MoveTowards(target, Step, rangeTolerance: 0f);
        var next = npc.Transform.Local.Position;
        await Assert.That(next.X).IsEqualTo(endAtSeam ? 112f : 111.5f).Within(.001f);
        await Assert.That(next.Z).IsEqualTo(Point(next.X, next.Y).Z).Within(.0001f);
        await Assert.That(npc.Movements.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(-.005f, false)]
    [Arguments(.005f, false)]
    [Arguments(.005f, true)]
    public async Task MoveTowards_SteepFaceAtCaveFoot_StillFindsAllWalkableFloors(float contactOffset, bool competingFloor)
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + .012f * (x - 111f));
        var baseFace = new[] { Point(106, 98), Point(117, 98), Point(111, 104) };
        // A steep contact intersects the starting ray, above or below the walking surface.
        var wall = new[] { new Vector3(110.99f, 99, 5 + contactOffset), new Vector3(111.01f, 99, 7 + contactOffset),
            new Vector3(111, 102, 6 + contactOffset) };
        var vertices = baseFace.Concat(wall).ToList();
        if (competingFloor)
            vertices.AddRange(baseFace.Select(p => p - Vector3.UnitZ * .007f));
        var mesh = new CryTriangleMesh(vertices.ToArray(), Enumerable.Range(0, vertices.Count).Select(i => (ushort)i).ToArray(), []);
        var npc = CreateDeckWalker(customMesh: mesh);
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        SetTerrainHeight(npc.ParentWorld.Template, 18);
        var from = Point(111, 100);
        npc.Transform.Local.SetPosition(from);
        npc.MoveTowards(Point(113.5f, 100), Step, rangeTolerance: 0);
        var next = npc.Transform.Local.Position;
        await Assert.That(next.X).IsEqualTo(competingFloor ? 111f : 111.5f).Within(.001f);
        await Assert.That(next.Z).IsEqualTo(Point(next.X, next.Y).Z).Within(.0001f);
        await Assert.That(npc.Movements.Count).IsEqualTo(competingFloor ? 0 : 1);
    }

    public enum Overlap { SameMesh, OtherPart, OtherPlacement }
    public enum OverlapRegion { Full, Target, NextFoot }

    [Test]
    [Arguments(Overlap.SameMesh, OverlapRegion.Full)]
    [Arguments(Overlap.OtherPart, OverlapRegion.Full)]
    [Arguments(Overlap.OtherPlacement, OverlapRegion.Full)]
    [Arguments(Overlap.OtherPlacement, OverlapRegion.Target)]
    [Arguments(Overlap.OtherPlacement, OverlapRegion.NextFoot)]
    public async Task MoveTowards_CloseCaveFloors_AdvanceOnlyWithUniqueFootSupport(Overlap overlap, OverlapRegion region)
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + .007f * (x - 111f));
        var lower = new[] { Point(101, 98), Point(121, 98), Point(111, 105) };
        Vector3[] upperFootprint = region switch
        {
            OverlapRegion.Target => [Point(112, 98), Point(121, 98), Point(114, 105)],
            OverlapRegion.NextFoot => [Point(111.1f, 99), Point(112.15f, 99), Point(111.52f, 101)],
            _ => lower
        };
        var upper = upperFootprint.Select(p => p + Vector3.UnitZ * .007f).ToArray();
        var mesh = new CryTriangleMesh([.. lower, .. upper], [0, 1, 2, 3, 4, 5], []);
        var npc = CreateDeckWalker(customMesh: mesh);
        var world = npc.ParentWorld.Template;
        if (overlap != Overlap.SameMesh)
        {
            CryGeometryPart Part(Vector3[] vertices) => new(new CryTriangleMesh(vertices, [0, 1, 2], []),
                Matrix4x4.Identity, 0x1000, "", "floor");
            var bottom = Part(lower);
            var top = Part(upper);
            var bounds = new CryBounds(mesh.Vertices.Aggregate(Vector3.Min), mesh.Vertices.Aggregate(Vector3.Max));
            if (overlap == Overlap.OtherPlacement)
            {
                var objects = world.Cells[0, 0].LoadedObjectDat;
                objects.AssetPathsList.Add(new AssetPath { Name = "objects/upper.cgf" });
                objects.PrefabsList.Add(new ObjectDataType1Brush
                {
                    PathId = 1, StartPos = bounds.Min, EndPos = bounds.Max,
                    Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1 }
                });
            }
            world.GeoData = new AiGeoDataManager(world)
            {
                GroundBrushGeometry = placement => new CryGeometryAsset(bounds, overlap == Overlap.OtherPart
                    ? [bottom, top] : placement.ModelUri == "objects/upper.cgf" ? [top] : [bottom])
            };
        }
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, 18);
        var start = Point(111f, 100f);
        var target = Point(113.8f, 100f);
        if (region != OverlapRegion.NextFoot)
            target.Z += .007f;
        npc.Transform.Local.SetPosition(start);

        if (region == OverlapRegion.Target)
        {
            // The upper placement begins at X 112.57 on this line. The lower
            // face still supports three consecutive half-metre steps by itself.
            for (var step = 1; step <= 3; step++)
            {
                npc.MoveTowards(target, Step, rangeTolerance: 0f);
                var foot = npc.Transform.Local.Position;
                await Assert.That(foot.X).IsEqualTo(start.X + step * Step).Within(.001f);
                await Assert.That(foot.Z).IsEqualTo(Point(foot.X, foot.Y).Z).Within(.0001f);
            }
        }

        var lastFoot = npc.Transform.Local.Position;
        var rotation = npc.Transform.Local.Rotation;
        var sent = npc.Movements.Count;
        for (var retry = 0; retry < 2; retry++)
        {
            var trace = NpcStepSurfaceTrace.DescribeStep(npc, target, Step).ToArray();
            await Assert.That(trace[1]).Contains("selected=HoldAmbiguousFloor");
            await Assert.That(npc.MoveTowards(target, Step, rangeTolerance: 0f)).IsFalse();
            await Assert.That(npc.Transform.Local.Position).IsEqualTo(lastFoot);
            await Assert.That(npc.Transform.Local.Rotation).IsEqualTo(rotation);
            await Assert.That(npc.Movements.Count).IsEqualTo(sent);
        }

        // An explicit authored route still controls its own height.
        npc.MoveTowards(target, 4f, rangeTolerance: 0f, authoredPathZ: true);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(target);
        await Assert.That(npc.Movements.Count).IsEqualTo(sent + 1);
    }

    [Test]
    public async Task MoveTowards_InclinedCaveTargetAcrossGap_UsesTerrain()
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + .0111f * (x - 111f));
        // The first half-metre stays on the near face. The target is across
        // a gap on a second coplanar triangle of the same placed mesh.
        var mesh = new CryTriangleMesh(
            [Point(108, 99), Point(112, 99), Point(110, 102),
             Point(112, 99), Point(116, 99), Point(114, 102)], [0, 1, 2, 3, 4, 5], []);
        var npc = CreateDeckWalker(customMesh: mesh);
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        SetTerrainHeight(npc.ParentWorld.Template, 18);
        npc.Transform.Local.SetPosition(Point(110f, 100f));
        npc.MoveTowards(Point(114f, 100f), Step, rangeTolerance: 0f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(18f);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MoveTowards_GentleCaveTargetOutsidePlacement_SkipsModelRead(bool distant)
    {
        var reads = new List<string>();
        var npc = CreateDeckWalker(overlappingModelReads: reads);
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, 18);
        var start = new Vector3(111f, 100f, DeckZ);
        var dx = distant ? 160f : 8f;
        npc.Transform.Local.SetPosition(start);
        npc.MoveTowards(start + new Vector3(dx, 0f, dx * .0111f), Step, rangeTolerance: 0f);

        await Assert.That(npc.Movements.Single().Z).IsEqualTo(18f);
        await Assert.That(reads).IsEmpty();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task MoveTowards_CrossesGentlyInclinedCaveFacesOnlyWhenConnected(bool gap, bool reverse)
    {
        // Sanddeep faces 504, 543 and 542, translated to the fixture. Parts of the middle face
        // outside the walked segment are more than one centimetre above or below the starting foot.
        Vector3[] vertices = [
            new(116.952f, 103.036f, 5.938305f), new(110.311f, 99.625f, 6.004345f),
            new(112.247f, 96.074f, 6.016674f), new(112.863f, 105.722f, 5.98773f),
            new(108.349f, 103.405f, 6.00918f)];
        ushort[] indices = gap ? [0, 1, 2, 3, 4, 1] : [0, 1, 2, 1, 0, 3, 3, 4, 1];
        var npc = CreateDeckWalker(customMesh: new CryTriangleMesh(vertices, indices, []));
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, 18);

        var from = new Vector3(111f, 99.6f, DeckZ);
        var to = new Vector3(110.426f, 100.42f, DeckZ + 0.00304f);
        if (reverse)
            (from, to) = (to, from);
        npc.Transform.Local.SetPosition(from);
        npc.MoveTowards(to, 1.01f, rangeTolerance: 0f);
        var expected = gap ? 18f : to.Z;
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(expected).Within(0.0005f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expected).Within(0.0005f);
    }

    [Test]
    public async Task MoveTowards_CaveFloorBelowTerrain_LeavesOnAnAuthoredAscent()
    {
        var npc = CreateDeckWalker();
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        SetTerrainHeight(npc.ParentWorld.Template, 18);
        var destination = new Vector3(111.5f, 100f, 18f);

        npc.MoveTowards(destination, 13f, rangeTolerance: 0f, authoredPathZ: true);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(destination);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(18f);
    }

    [Test]
    public async Task MoveTowards_TerrainAboveCaveFloor_StaysOnTheSurface()
    {
        var npc = CreateDeckWalker();
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetTerrainHeight(world, 18);
        npc.Transform.Local.SetPosition(112f, 100f, 18f);

        npc.MoveTowards(new Vector3(109f, 100f, 18f), Step, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(111.5f, 100f, 18f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(18f);
    }

    private static void SetTerrainHeight(WorldTemplate world, ushort height)
    {
        var samples = world.Cells[0, 0].HeightMap;
        for (var x = 0; x < samples.GetLength(0); x++)
        for (var y = 0; y < samples.GetLength(1); y++)
            samples[x, y] = height;
    }

    [Test]
    [Arguments(10, 2, DeckZ, DeckZ)]
    [Arguments(2, 10, DeckZ, DeckZ)]
    [Arguments(10, 2, 5f, 7f)]
    public async Task MoveTowards_AtCaveMouth_StaysOnItsStartingSurface(int west, int east, float footZ, float expectedZ)
    {
        var reads = new List<string>();
        var npc = CreateDeckWalker(overlappingModelReads: reads);
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        var samples = world.Cells[0, 0].HeightMap;
        for (var y = 0; y < samples.GetLength(1); y++)
        {
            samples[55, y] = (ushort)west;
            samples[56, y] = (ushort)east;
        }
        // Terrain crosses the flat floor at X 111. The two feet lie 0.25 m on either side.
        npc.Transform.Local.SetPosition(111.25f, 100f, footZ);
        npc.MoveTowards(new Vector3(109.25f, 100f, footZ), Step, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.75f, 100f, expectedZ));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expectedZ);
        await Assert.That(reads.Count > 0).IsEqualTo(footZ == DeckZ);
    }

    [Test]
    public async Task MoveTowards_TerrainBelowDeck_StaysOnTerrain()
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, 0f);
        npc.MoveTowards(new Vector3(105f, 100f, 0f), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110f, 100f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_AuthoredDescentThroughDeck_UsesAuthoredHeight()
    {
        var npc = CreateDeckWalker();
        var destination = new Vector3(110.5f, 100f, 3f);
        npc.MoveTowards(destination, 4f, rangeTolerance: 0f, authoredPathZ: true);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(destination);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(3f);
    }

    [Test]
    [Arguments(0.0001f)]
    [Arguments(-0.0001f)]
    [Arguments(0.002f)]
    [Arguments(-0.002f)]
    public async Task MoveTowards_SmallTargetHeightDifference_KeepsDeckHeight(float heightDifference)
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(110.5f, 100f, DeckZ + heightDifference), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position.X).IsEqualTo(110.5f).Within(0.0001f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(DeckZ);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(DeckZ);
    }

    [Test]
    [Arguments(0.5f)]
    [Arguments(2f)]
    public async Task MoveTowards_ClearlyLowerTarget_UsesTerrainHeight(float targetDrop)
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, DeckZ);
        // For the 0.5 m descent, interpolation alone changes the first step by less than 1 cm.
        npc.MoveTowards(new Vector3(78.5f, 100f, DeckZ - targetDrop), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position.X).IsLessThan(110.5f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(0f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_MissingModelOverlapsDeck_UsesNextSupportingBrush()
    {
        var modelReads = new List<string>();
        var npc = CreateDeckWalker(overlappingModelReads: modelReads);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(110.5f, 100f, DeckZ), Step, rangeTolerance: 0f);

        await Assert.That(modelReads.SequenceEqual(["objects/missing.cgf", "objects/deck.cgf"])).IsTrue();
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 100f, DeckZ));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(DeckZ);
    }

    [Test]
    [Arguments(true, DeckZ)]
    [Arguments(false, DeckZ - 2f)]
    public async Task MoveTowards_LowerWaypoint_PreservesFloorOnlyWithSolidSupport(bool hasDeck, float expected)
    {
        var npc = CreateDeckWalker(hasDeck);
        var world = npc.ParentWorld.Template;
        var nav = world.ZoneBaiLoader[1];
        nav.VertexMissionReaders.Clear();
        var net = nav.NetMissionReaders.Single();
        net.NodeDescriptorList.Clear();
        net.NodeDescriptorList[1] = new NodeDescriptor(net)
        {
            Id = 1, NavigationType = BaiNavigationType.WaypointHuman, Pos = new(111.5f, 100f, DeckZ - 2f)
        };
        await Assert.That(world.GeoData.TryGetGroundSurface(new Vector3(111.5f, 100f, DeckZ), out var sample)).IsTrue();
        await Assert.That(sample.Source).IsEqualTo(GroundSurfaceSource.NavigationNode);
        await Assert.That(sample.Height).IsEqualTo(DeckZ - 2f);

        npc.MoveTowards(new Vector3(109f, 100f, DeckZ), Step, rangeTolerance: 0f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(0, -1)]
    [Arguments(1, 1)]
    [Arguments(1, -1)]
    public async Task MoveTowards_DeckAcrossCellBoundary_KeepsFloorUntilItsEdge(int ownerCellX, int direction)
    {
        var npc = CreateDeckWalker();
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        var west = world.Cells[0, 0];
        var east = new WorldCell(1, 0, world);
        SetPrivateMember(east, nameof(WorldCell.HeightMap), west.HeightMap);
        SetPrivateMember(east, nameof(WorldCell.Loaded), true);
        world.CellX = 2;
        world.Cells = new WorldCell[,] { { west }, { east } };

        // Move this single placement across the cell boundary. Only its owner has the brush record.
        var objects = west.LoadedObjectDat;
        west.LoadedObjectDat = null;
        world.Cells[ownerCellX, 0].LoadedObjectDat = objects;
        var brush = (ObjectDataType1Brush)objects.PrefabsList.Single();
        var localOffset = WorldManager.CELL_SIZE - 111f - ownerCellX * WorldManager.CELL_SIZE;
        brush.StartPos += Vector3.UnitX * localOffset;
        brush.EndPos += Vector3.UnitX * localOffset;
        brush.Matrix3X4 = new Matrix3x4 { M11 = 1f, M22 = 1f, M33 = 1f, M14 = localOffset };

        var start = new Vector3(WorldManager.CELL_SIZE - direction * 0.25f, 100f, DeckZ);
        var destination = start + Vector3.UnitX * (direction * 2f);
        var next = start + Vector3.UnitX * (Step * direction);
        await Assert.That(world.GeoData.TryGetGroundSurface(next, out var ground)).IsTrue();
        await Assert.That(ground.Source).IsEqualTo(GroundSurfaceSource.Terrain);
        await Assert.That(ground.Height).IsEqualTo(0f);
        npc.Transform.Local.SetPosition(start);
        for (var step = 1; step <= 3; step++)
        {
            npc.MoveTowards(destination, Step, rangeTolerance: 0f);
            await Assert.That(npc.Transform.Local.Position).IsEqualTo(start + Vector3.UnitX * (step * Step * direction));
            await Assert.That(npc.Movements[^1].Z).IsEqualTo(DeckZ);
        }

        var edge = npc.Transform.Local.Position - Vector3.UnitY;
        npc.MoveTowards(edge, 1f, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(edge.X, edge.Y, 0f));
    }

    public enum StripSurface { Floor, Gap, DownwardFace }

    [Test]
    [Arguments(StripSurface.Floor, 1, DeckZ)]
    [Arguments(StripSurface.Floor, -1, DeckZ)]
    [Arguments(StripSurface.Gap, 1, 0f)]
    [Arguments(StripSurface.Gap, -1, 0f)]
    [Arguments(StripSurface.DownwardFace, 1, 0f)]
    public async Task MoveTowards_ManyFloorFaces_OnlyCrossesTheConnectedStrip(StripSurface middle, int direction, float expected)
    {
        // The distant band connects both ends even when the straight path has a gap.
        var vertices = new List<Vector3>();
        var indices = new List<ushort>();
        for (var column = 0; column < 6; column++)
        {
            var x = 110.45f + column * 0.12f;
            foreach (var y in new[] { 99.5f, 100.25f, 100.5f })
                vertices.Add(new Vector3(x, y, DeckZ));

            if (column == 0)
                continue;
            var previous = (ushort)((column - 1) * 3);
            for (var band = 0; band < 2; band++)
            {
                var surface = column == 3 && band == 0 ? middle : StripSurface.Floor;
                if (surface == StripSurface.Gap)
                    continue;

                var a = (ushort)(previous + band);
                var b = (ushort)(a + 3);
                var c = (ushort)(b + 1);
                var d = (ushort)(a + 1);
                indices.AddRange(surface == StripSurface.Floor ? [a, b, c, a, c, d] : [a, c, b, a, d, c]);
            }
        }

        var npc = CreateDeckWalker(customMesh: new CryTriangleMesh([.. vertices], [.. indices], []));
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        var start = new Vector3(direction > 0 ? 110.5f : 111f, 100f, DeckZ);
        npc.Transform.Local.SetPosition(start);
        npc.MoveTowards(start + Vector3.UnitX * direction, Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(expected);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expected);
    }

    private MovementProbeNpc CreateDeckWalker(bool hasDeck = true, bool hasGap = false, List<string> overlappingModelReads = null, CryTriangleMesh customMesh = null)
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate(worldName: "main_world");
        var template = manager.GetWorldTemplateByZoneKey(100);
        var skills = new SkillManager(null, null);
        SetPrivateMember(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        UseMovementService(manager);
        UseMovementService(skills);

        var net = new NetMissionReader(Stream.Null, 1);
        net.NodeDescriptorList[3597] = new NodeDescriptor(net)
        {
            Id = 3597, NavigationType = BaiNavigationType.WaypointHuman, Pos = new(116.05f, 100.257f, DeckZ)
        };
        net.NodeDescriptorList[2704] = new NodeDescriptor(net)
        {
            Id = 2704, NavigationType = BaiNavigationType.Triangular, Pos = new(106.595f, 103.688f, DeckZ - 1.09752f)
        };
        var bai = new BaseBaiLoader(template);
        bai.NetMissionReaders.Add(net);
        var vertices = new VertexMissionReader(Stream.Null, 1);
        vertices.ObstacleDataDescriptorList.Add(new ObstacleDataDescriptor(1)
        {
            Pos = new(106.883f, 99.101f, DeckZ - 4.24935f)
        });
        bai.VertexMissionReaders.Add(vertices);
        template.ZoneBaiLoader.Add(1, bai);

        var bounds = new CryBounds(new(107f, 99.5f, DeckZ), new(114.5f, 100.5f, DeckZ));
        // The continuous deck shares its diagonal. The gap splits the same brush into disconnected faces.
        var mesh = customMesh ?? (hasGap
            ? new CryTriangleMesh(
                [bounds.Min, new(110.65f, 99.5f, DeckZ), new(110.65f, 100.5f, DeckZ), new(107f, 100.5f, DeckZ),
                 new(110.85f, 99.5f, DeckZ), new(114.5f, 99.5f, DeckZ), bounds.Max, new(110.85f, 100.5f, DeckZ)],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7], [])
            : new CryTriangleMesh(
                [bounds.Min, new(114.5f, 99.5f, DeckZ), bounds.Max, new(107f, 100.5f, DeckZ)],
                [0, 1, 2, 0, 2, 3], []));
        if (customMesh != null)
            bounds = new CryBounds(mesh.Vertices.Aggregate(Vector3.Min), mesh.Vertices.Aggregate(Vector3.Max));
        var part = new CryGeometryPart(mesh, Matrix4x4.Identity, 4096, "", "deck");
        var geometry = new CryGeometryAsset(bounds, [part]);
        template.GeoData = new AiGeoDataManager(template)
        {
            GroundBrushGeometry = brush =>
            {
                var path = brush.ModelUri;
                overlappingModelReads?.Add(path);
                return path == "objects/deck.cgf" ? geometry : throw new FileNotFoundException("Missing fixture model.", path);
            }
        };
        if (hasDeck)
        {
            var objects = new ObjectsFile("main_world/cells/000_000/client/object.dat")
            {
                AssetPathsList = [new AssetPath { Name = "objects/deck.cgf" }],
                PrefabsList = [new ObjectDataType1Brush
                {
                    StartPos = bounds.Min, EndPos = bounds.Max, Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1 }
                }]
            };
            if (overlappingModelReads != null)
            {
                objects.AssetPathsList.Add(new AssetPath { Name = "objects/missing.cgf" });
                objects.PrefabsList.Insert(0, new ObjectDataType1Brush
                {
                    PathId = 1, StartPos = bounds.Min, EndPos = bounds.Max,
                    Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1 }
                });
            }
            template.Cells[0, 0].LoadedObjectDat = objects;
        }
        var npc = new MovementProbeNpc { Hp = 100, DisabledSetPosition = true };
        npc.ParentWorld = new WorldInstance(template, 1, true, npc.Transform.InstanceId);
        SetPrivateMember(npc.Transform, "_zoneId", 100u);
        npc.Transform.Local.SetPosition(112f, 100f, DeckZ);
        npc.Ai = new DummyAiCharacter { Owner = npc, HomePosition = npc.Transform.Local.Position };
        return npc;
    }

    private void UseMovementService<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        field.SetValue(null, manager);
        _restoreMovementServices.Add(() => field.SetValue(null, previous));
    }

    [After(Test)]
    public void RestoreMovementServices()
    {
        foreach (var restore in _restoreMovementServices)
            restore();
        _restoreMovementServices.Clear();
    }
}
