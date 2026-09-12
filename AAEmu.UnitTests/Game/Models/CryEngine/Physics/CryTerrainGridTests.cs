using System.Numerics;
using System.Text;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.CryEngine.Physics;

public sealed class CryTerrainGridTests
{
    [Test]
    [Arguments(0)]
    [Arguments(16)]
    [Arguments(30)]
    public async Task Read_UsesElevenHeightBitsAndDirectSurfaceIds(int surface)
    {
        using var stream = Map(2, Node(2, [Pack(3, surface), Pack(3, surface), Pack(3, surface), Pack(3, surface)],
            offset: 0.013f, range: 0.00123f, surfaces: [27]));
        var grid = CryTerrainGrid.Read(stream, new Vector2(1000, 2000));
        var expected = 0.013f + 96 * 0.00123f;
        await Assert.That(grid.GridSize).IsEqualTo(2);
        await Assert.That(grid.UnitSize).IsEqualTo(2f);
        await Assert.That(grid.Origin).IsEqualTo(new Vector2(1000, 2000));
        await Assert.That(MathF.Abs(grid.SampleHeight(1001, 2001) - expected) < 0.000001f).IsTrue();
        await Assert.That(grid.Raycast(new Vector3(1001, 2001, 10), -Vector3.UnitZ, 20, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.MaterialId).IsEqualTo(surface);
        await Assert.That(MathF.Abs(hit.Position.Z - expected) < 0.000001f).IsTrue();
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(stream.CanRead).IsTrue();
    }

    [Test]
    [Arguments(0.5f, 0.5f, 1.5f, 0)]
    [Arguments(1.5f, 1.5f, 6.5f, 1)]
    [Arguments(1f, 1f, 3f, 0)]
    public async Task Raycast_UsesTheFixedAntiDiagonal(float x, float y, float expectedHeight, int triangle)
    {
        using var stream = Map(2, Node(2, [Pack(0), Pack(4), Pack(2), Pack(10)]));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.SampleHeight(x, y)).IsEqualTo(expectedHeight);
        await Assert.That(grid.Raycast(new Vector3(x, y, 20), new Vector3(0, 0, -3), 40, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.Position.Z).IsEqualTo(expectedHeight);
        await Assert.That(hit.Distance).IsEqualTo(20 - expectedHeight);
        await Assert.That(hit.TriangleIndex).IsEqualTo(triangle);
        var normal = triangle == 0 ? Vector3.Normalize(new Vector3(-1, -2, 1)) : Vector3.Normalize(new Vector3(-3, -4, 1));
        await Assert.That(Vector3.Distance(hit.Normal, normal) < 0.000001f).IsTrue();
    }

    [Test]
    [Arguments(0.25f, 0.25f)]
    [Arguments(1.75f, 1.75f)]
    public async Task Hole_RemovesBothTrianglesWithoutDependingOnTheTreeSummary(float x, float y)
    {
        using var stream = Map(2, Node(2, [Pack(3, 31), Pack(3), Pack(3), Pack(3)], hasHoles: false));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(float.IsNaN(grid.SampleHeight(x, y))).IsTrue();
        await Assert.That(grid.Raycast(new Vector3(x, y, 20), -Vector3.UnitZ, 30, out _))
            .IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task Hole_DoesNotRemoveItsNeighbourOrChangeVertexHeight()
    {
        using var stream = Map(4, Node(3,
            [Pack(3, 31), Pack(3), Pack(3), Pack(3, 20), Pack(3), Pack(3), Pack(3), Pack(3), Pack(3)]));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(float.IsNaN(grid.SampleHeight(1, 1))).IsTrue();
        await Assert.That(grid.SampleHeight(3, 1)).IsEqualTo(3f);
        await Assert.That(grid.Raycast(new Vector3(3, 1, 20), -Vector3.UnitZ, 30, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.MaterialId).IsEqualTo(20);
    }

    [Test]
    public async Task CoarseNode_InterpolatesFullResolutionVerticesBeforeTriangleTests()
    {
        using var stream = Map(4, Node(2, [Pack(0), Pack(0), Pack(0), Pack(16)]));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        // The source bilinear surface is sampled at each 2m vertex. A 2m square then uses triangles.
        await Assert.That(grid.SampleHeight(2, 2)).IsEqualTo(4f);
        await Assert.That(grid.SampleHeight(0.5f, 0.5f)).IsEqualTo(0f);
        await Assert.That(grid.SampleHeight(1.5f, 1.5f)).IsEqualTo(2f);
    }

    [Test]
    public async Task CoarseNode_ReplicatesTheLowerCornerHoleAcrossItsSquares()
    {
        using var stream = Map(4, Node(2, [Pack(0, 31), Pack(0), Pack(0), Pack(16)]));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(float.IsNaN(grid.SampleHeight(3, 3))).IsTrue();
        await Assert.That(grid.Raycast(new Vector3(3, 3, 20), -Vector3.UnitZ, 30, out _))
            .IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task SampleRawHeight_FloorsIntegerMetresAndKeepsTheHeightUnderAHole()
    {
        using var stream = Map(4, Node(3,
            [Pack(3, 31), Pack(4), Pack(5), Pack(6), Pack(7), Pack(8), Pack(9), Pack(10), Pack(11)]));
        var grid = CryTerrainGrid.Read(stream, new Vector2(100, 200));
        await Assert.That(grid.SampleRawHeight(101, 201)).IsEqualTo(3f);
        await Assert.That(grid.SampleRawHeight(102, 201)).IsEqualTo(6f);
        await Assert.That(grid.SampleRawHeight(104, 204)).IsEqualTo(11f);
        await Assert.That(float.IsNaN(grid.SampleHeight(101, 201))).IsTrue();
        await Assert.That(float.IsNaN(grid.SampleRawHeight(99, 201))).IsTrue();
    }

    [Test]
    public async Task SampleRawHeight_CoarseNode_InterpolatesTheFullResolutionVertex()
    {
        using var stream = Map(4, Node(2, [Pack(0, 31), Pack(0), Pack(0), Pack(16)]));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.SampleRawHeight(3, 3)).IsEqualTo(4f);
        await Assert.That(grid.SampleRawHeight(1, 1)).IsEqualTo(0f);
    }

    [Test]
    public async Task Raycast_FindsFirstObliqueHitAndUsesWorldDistance()
    {
        using var stream = Map(4, Node(3, Enumerable.Repeat(Pack(2), 9).ToArray()));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.Raycast(new Vector3(-2, 1, 6), new Vector3(1, 0, -1), 10, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(Vector3.Distance(hit.Position, new Vector3(2, 1, 2)) < 0.000001f).IsTrue();
        await Assert.That(MathF.Abs(hit.Distance - MathF.Sqrt(32)) < 0.000001f).IsTrue();
    }

    [Test]
    [Arguments(0f, 0f, 0f, CryIntersection.Indeterminate)]
    [Arguments(0f, 0f, 1f, CryIntersection.Clear)]
    [Arguments(1f, 0f, 0f, CryIntersection.Clear)]
    [Arguments(float.NaN, 0f, -1f, CryIntersection.Indeterminate)]
    public async Task Raycast_HandlesInvalidAndNonIntersectingDirections(float x, float y, float z, CryIntersection expected)
    {
        using var stream = Map(2, Node(2, Enumerable.Repeat(Pack(2), 4).ToArray()));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.Raycast(new Vector3(1, 1, 10), new Vector3(x, y, z), 20, out _)).IsEqualTo(expected);
    }

    [Test]
    public async Task Raycast_RespectsDistanceAndUpwardFacingTerrain()
    {
        using var stream = Map(2, Node(2, Enumerable.Repeat(Pack(2), 4).ToArray()));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.Raycast(new Vector3(1, 1, 10), -Vector3.UnitZ, 7.99f, out _)).IsEqualTo(CryIntersection.Clear);
        await Assert.That(grid.Raycast(new Vector3(1, 1, 10), -Vector3.UnitZ, 8, out _)).IsEqualTo(CryIntersection.Intersects);
        await Assert.That(grid.Raycast(new Vector3(1, 1, 0), Vector3.UnitZ, 10, out _)).IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task MissingNode_IsIndeterminateOnlyWhenTheRayCrossesItsBounds()
    {
        using var stream = Map(2, Node(0, []));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(float.IsNaN(grid.SampleHeight(1, 1))).IsTrue();
        await Assert.That(grid.Raycast(new Vector3(1, 1, 20), -Vector3.UnitZ, 40, out _)).IsEqualTo(CryIntersection.Indeterminate);
        await Assert.That(grid.Raycast(new Vector3(3, 1, 20), -Vector3.UnitZ, 40, out _)).IsEqualTo(CryIntersection.Clear);
        await Assert.That(float.IsNaN(grid.SampleHeight(float.NaN, 1))).IsTrue();
        await Assert.That(float.IsNaN(grid.SampleHeight(-1, 1))).IsTrue();
    }

    [Test]
    [Arguments(0.5f, 10f, 1f, -2f, CryIntersection.Indeterminate)]
    [Arguments(3.5f, 5f, -1f, -2f, CryIntersection.Intersects)]
    public async Task MissingNode_OnlyObscuresHitsAfterTheMissingArea(float x, float z, float dx, float dz, CryIntersection expected)
    {
        using var stream = Map(4, Node(0, []),
            (new Vector2(2, 0), 2, Node(2, Enumerable.Repeat(Pack(3), 4).ToArray())));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.Raycast(new Vector3(x, 1, z), new Vector3(dx, 0, dz), 20, out _)).IsEqualTo(expected);
    }

    [Test]
    public async Task Read_SectorSeamUsesThePositiveSectorVertex()
    {
        using var stream = Map(4, Node(0, []),
            (new Vector2(2, 0), 2, Node(2, Enumerable.Repeat(Pack(4), 4).ToArray())),
            (Vector2.Zero, 2, Node(2, Enumerable.Repeat(Pack(2), 4).ToArray())));
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.SampleHeight(2, 1)).IsEqualTo(4f);
        await Assert.That(grid.SampleHeight(1, 1)).IsEqualTo(3f);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Read_RejectsUnsupportedOrTruncatedData(int failure)
    {
        using var original = Map(2, Node(2, Enumerable.Repeat(Pack(2), 4).ToArray()));
        var bytes = original.ToArray();
        if (failure == 0) bytes[0] = 25;
        if (failure == 1) bytes[160] = 7;
        if (failure == 2) bytes[2] = 1;
        if (failure == 3) bytes = bytes[..^1];
        using var stream = new MemoryStream(bytes);
        if (failure == 3)
            await Assert.That(() => CryTerrainGrid.Read(stream, Vector2.Zero)).Throws<EndOfStreamException>();
        else
            await Assert.That(() => CryTerrainGrid.Read(stream, Vector2.Zero)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Read_ExactClientHoleAndFullHeightPrecision()
    {
        var root = Environment.GetEnvironmentVariable("AAEMU_TERRAIN_CLIENT_ROOT");
        Skip.Unless(!string.IsNullOrEmpty(root), "Set AAEMU_TERRAIN_CLIENT_ROOT to extracted client terrain files.");
        var path = Path.Combine(root!, "game/worlds/main_world/cells/021_009/client/terrain/heightmap.dat");
        using var stream = File.OpenRead(path);
        var grid = CryTerrainGrid.Read(stream, Vector2.Zero);
        await Assert.That(grid.GridSize).IsEqualTo(513);
        // Exact source hole: leaf at (4992,6912), sample coordinates (8,18), root at (4096,6144).
        await Assert.That(float.IsNaN(grid.SampleHeight(913, 805))).IsTrue();
        await Assert.That(grid.Raycast(new Vector3(913, 805, 1000), -Vector3.UnitZ, 1000, out _)).IsEqualTo(CryIntersection.Clear);
        await Assert.That(grid.Raycast(new Vector3(911, 805, 1000), -Vector3.UnitZ, 1000, out var hit)).IsEqualTo(CryIntersection.Intersects);
        await Assert.That(MathF.Abs(hit.Position.Z - grid.SampleHeight(911, 805)) < 0.0001f).IsTrue();
        // Native node offset 370.1932068, range 0.0018549523. The diagonal endpoints contain 16384 and 8768 height bits.
        await Assert.That(MathF.Abs(hit.Position.Z - 393.5210876464844f) < 0.0001f).IsTrue();
    }

    private static ushort Pack(int height, int surface = 0) => (ushort)((height << 5) | surface);

    private sealed record NodeData(int Size, ushort[] Packed, float Offset, float Range, byte[] Surfaces, bool HasHoles);
    private static NodeData Node(int size, ushort[] packed, float offset = 0, float range = 1f / 32,
        byte[] surfaces = null, bool hasHoles = false) => new(size, packed, offset, range, surfaces ?? [], hasHoles);

    private static MemoryStream Map(int width, NodeData node, params (Vector2 Origin, int Width, NodeData Node)[] children)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(24);
        writer.Write(0); // patched chunk size
        writer.Write(4096);
        writer.Write(2);
        writer.Write(64);
        writer.Write(128);
        writer.Write(0.0625f);
        writer.Write(100f);
        writer.Write(new byte[128]);
        WriteNode(Vector2.Zero, width, node);
        foreach (var child in children) WriteNode(child.Origin, child.Width, child.Node);
        stream.Position = 4;
        writer.Write((int)stream.Length);
        stream.Position = 0;
        return stream;

        void WriteNode(Vector2 relative, int nodeWidth, NodeData data)
        {
            var origin = new Vector2(6144, 4096) + relative;
            writer.Write(5);
            WriteVector(writer, new Vector3(origin, -100));
            WriteVector(writer, new Vector3(origin + new Vector2(nodeWidth), 100));
            writer.Write(data.HasHoles);
            writer.Write(data.Offset);
            writer.Write(data.Range);
            writer.Write(data.Size);
            writer.Write(data.Surfaces.Length);
            foreach (var sample in data.Packed) writer.Write(sample);
            writer.Write(new byte[20]);
            writer.Write(data.Surfaces);
            writer.Write(new byte[36]);
        }
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
}
