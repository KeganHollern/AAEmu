using System.Numerics;
using System.Text;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>
/// The r208022 heightfield, separate from the quantized WorldCell height cache.
/// Samples have eleven height bits and five surface bits. Surface 31 removes both triangles.
/// </summary>
public sealed class CryTerrainGrid
{
    private const byte Hole = 31;
    private const byte Unavailable = byte.MaxValue;
    private readonly float[] _heights;
    private readonly byte[] _surfaces;

    public Vector2 Origin { get; }
    public float UnitSize { get; }
    /// <summary>The number of vertices on each side, including the final edge.</summary>
    public int GridSize { get; }
    public CryBounds Bounds { get; }

    private CryTerrainGrid(Vector2 origin, float unitSize, int gridSize, CryBounds bounds)
    {
        Origin = origin;
        UnitSize = unitSize;
        GridSize = gridSize;
        Bounds = bounds;
        _heights = new float[checked(gridSize * gridSize)];
        Array.Fill(_heights, float.NaN);
        _surfaces = new byte[checked((gridSize - 1) * (gridSize - 1))];
        Array.Fill(_surfaces, Unavailable);
    }

    /// <summary>
    /// Reads a little-endian version 24 heightmap with version 5 nodes. The file's root XY
    /// bounds are local to its streamed terrain region, so the caller supplies the cell origin.
    /// Leaves the input open. Unsupported or incomplete data throws InvalidDataException/EndOfStreamException.
    /// </summary>
    public static CryTerrainGrid Read(System.IO.Stream heightmap, Vector2 cellOrigin)
    {
        if (!Finite(cellOrigin.X) || !Finite(cellOrigin.Y))
            throw new ArgumentOutOfRangeException(nameof(cellOrigin));
        using var reader = new BinaryReader(heightmap, Encoding.UTF8, true);
        var start = heightmap.Position;
        var version = reader.ReadByte();
        reader.ReadByte();
        var flags = reader.ReadByte();
        reader.ReadByte();
        var chunkSize = reader.ReadInt32();
        var worldUnits = reader.ReadInt32();
        var unitSize = reader.ReadInt32();
        var sectorSize = reader.ReadInt32();
        var sectorCount = reader.ReadInt32();
        reader.ReadSingle();
        reader.ReadSingle();
        if (version != 24 || (flags & 1) != 0 || chunkSize < 160 + 81 ||
            worldUnits <= 0 || unitSize <= 0 || sectorSize <= 0 || sectorCount <= 0 ||
            sectorSize % unitSize != 0 || !BitOperations.IsPow2((uint)(sectorSize / unitSize)))
            throw new InvalidDataException("Unsupported terrain heightmap header.");
        var errorBytes = BitOperations.Log2((uint)(sectorSize / unitSize)) * 4;
        var nodeTailBytes = errorBytes + 36;
        var end = checked(start + chunkSize);
        if (end > heightmap.Length)
            throw new EndOfStreamException("Truncated terrain heightmap.");
        Skip(reader, 128, end);

        CryTerrainGrid grid = null;
        var fileOrigin = Vector2.Zero;
        while (heightmap.Position < end)
        {
            if (end - heightmap.Position < 45 + nodeTailBytes)
                throw new InvalidDataException("Truncated terrain node.");
            var nodeVersion = reader.ReadInt32();
            var min = ReadVector(reader);
            var max = ReadVector(reader);
            reader.ReadByte(); // bHasHoles is a tree summary. Each square uses its own surface ID.
            var offset = reader.ReadSingle();
            var range = reader.ReadSingle();
            var size = reader.ReadInt32();
            var surfaceCount = reader.ReadInt32();
            if (nodeVersion != 5 || !Finite(min) || !Finite(max) ||
                max.X <= min.X || max.Y <= min.Y || max.Z < min.Z ||
                !Finite(offset) || !Finite(range) || range < 0 ||
                size < 0 || size == 1 || size > 4097 || surfaceCount < 0 || surfaceCount > 31)
                throw new InvalidDataException("Unsupported terrain node.");

            if (grid == null)
            {
                var units = ExactUnits(max.X - min.X, unitSize);
                if (units != ExactUnits(max.Y - min.Y, unitSize) || units > 4096)
                    throw new InvalidDataException("Invalid terrain root bounds.");
                fileOrigin = new Vector2(min.X, min.Y);
                grid = new CryTerrainGrid(cellOrigin, unitSize, units + 1,
                    new CryBounds(new Vector3(cellOrigin, min.Z),
                        new Vector3(cellOrigin + new Vector2(max.X - min.X, max.Y - min.Y), max.Z)));
            }

            var sampleCount = checked(size * size);
            if ((long)sampleCount * 2 + nodeTailBytes + surfaceCount > end - heightmap.Position)
                throw new EndOfStreamException("Truncated terrain samples.");
            var packed = new ushort[sampleCount];
            for (var i = 0; i < packed.Length; i++)
                packed[i] = reader.ReadUInt16();
            // log2(sector units) geometry-error floats, the surface list, then 36 reserved bytes.
            // The surface list does not remap the five low sample bits.
            Skip(reader, nodeTailBytes + surfaceCount, end);
            if (size != 0)
                grid.AddNode(min, max, fileOrigin, size, packed, offset, range);
        }
        return grid ?? throw new InvalidDataException("The heightmap has no root node.");
    }

    /// <summary>Gets the triangle height at world XY. Holes and unavailable data return NaN.</summary>
    public float SampleHeight(float x, float y)
    {
        if (!TryLocate(x, y, out var cellX, out var cellY, out var fractionX, out var fractionY) ||
            _surfaces[SurfaceIndex(cellX, cellY)] >= Hole)
            return float.NaN;
        var h00 = Height(cellX, cellY);
        var h10 = Height(cellX + 1, cellY);
        var h01 = Height(cellX, cellY + 1);
        var h11 = Height(cellX + 1, cellY + 1);
        return fractionX + fractionY <= 1
            ? (1 - fractionX - fractionY) * h00 + fractionX * h10 + fractionY * h01
            : (1 - fractionY) * h10 + (fractionX + fractionY - 1) * h11 + (1 - fractionX) * h01;
    }

    /// <summary>
    /// Matches I3DEngine +0x204: floor integer metre coordinates to the unit grid and read that
    /// vertex, including heights under holes. Unavailable data returns NaN instead of native zero.
    /// </summary>
    public float SampleRawHeight(int worldX, int worldY)
    {
        if (worldX < Bounds.Min.X || worldY < Bounds.Min.Y || worldX > Bounds.Max.X || worldY > Bounds.Max.Y)
            return float.NaN;
        var x = (int)MathF.Floor((worldX - Origin.X) / UnitSize);
        var y = (int)MathF.Floor((worldY - Origin.Y) / UnitSize);
        return Height(x, y);
    }

    /// <summary>Traces the terrain's upward-facing triangles. Distance is measured in world metres.</summary>
    public CryIntersection Raycast(Vector3 origin, Vector3 direction, float maxDistance, out CryRayHit hit)
    {
        hit = default;
        if (!Finite(origin) || !Finite(direction) || !Finite(maxDistance) || maxDistance < 0 ||
            !Finite(direction.LengthSquared()) || direction.LengthSquared() == 0)
            return CryIntersection.Indeterminate;
        direction = Vector3.Normalize(direction);
        if (!Clip(origin, direction, Bounds.Min, Bounds.Max, maxDistance, out var enter, out var leave))
            return CryIntersection.Clear;
        var first = origin + direction * enter;
        var last = origin + direction * leave;
        var x0 = Cell(MathF.Min(first.X, last.X), Origin.X);
        var y0 = Cell(MathF.Min(first.Y, last.Y), Origin.Y);
        var x1 = Cell(MathF.Max(first.X, last.X), Origin.X);
        var y1 = Cell(MathF.Max(first.Y, last.Y), Origin.Y);
        var nearest = maxDistance;
        var missingBefore = float.PositiveInfinity;
        var found = false;
        var candidate = default(CryRayHit);
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            var min = new Vector3(Origin.X + x * UnitSize, Origin.Y + y * UnitSize, Bounds.Min.Z);
            var max = new Vector3(min.X + UnitSize, min.Y + UnitSize, Bounds.Max.Z);
            if (!Clip(origin, direction, min, max, nearest, out var squareEnter, out _))
                continue;
            var surface = _surfaces[SurfaceIndex(x, y)];
            if (surface == Hole)
                continue;
            var a = new Vector3(min.X, min.Y, Height(x, y));
            var b = new Vector3(max.X, min.Y, Height(x + 1, y));
            var c = new Vector3(min.X, max.Y, Height(x, y + 1));
            var d = new Vector3(max.X, max.Y, Height(x + 1, y + 1));
            if (surface == Unavailable || !Finite(a) || !Finite(b) || !Finite(c) || !Finite(d))
            {
                missingBefore = MathF.Min(missingBefore, squareEnter);
                continue;
            }
            TestTriangle(a, b, c, 0);
            TestTriangle(b, d, c, 1);

            void TestTriangle(Vector3 v0, Vector3 v1, Vector3 v2, int triangle)
            {
                if (!IntersectTriangle(origin, direction, v0, v1, v2, nearest, out var distance, out var normal))
                    return;
                if (found && distance == nearest)
                    return; // The native ray routine tests the lower triangle first on the diagonal.
                nearest = distance;
                found = true;
                candidate = new CryRayHit(distance, origin + direction * distance, normal, surface,
                    SurfaceIndex(x, y) * 2 + triangle);
            }
        }
        if (missingBefore <= nearest)
            return CryIntersection.Indeterminate;
        if (!found)
            return CryIntersection.Clear;
        hit = candidate;
        return CryIntersection.Intersects;
    }

    private void AddNode(Vector3 min, Vector3 max, Vector2 fileOrigin, int size, ushort[] packed,
        float offset, float range)
    {
        var nodeUnits = ExactUnits(max.X - min.X, UnitSize);
        var x0 = ExactUnits(min.X - fileOrigin.X, UnitSize, true);
        var y0 = ExactUnits(min.Y - fileOrigin.Y, UnitSize, true);
        if (nodeUnits != ExactUnits(max.Y - min.Y, UnitSize) || nodeUnits % (size - 1) != 0 ||
            x0 + nodeUnits >= GridSize || y0 + nodeUnits >= GridSize)
            throw new InvalidDataException("Terrain node does not fit the heightfield.");
        var step = nodeUnits / (size - 1);
        if ((step & (step - 1)) != 0)
            throw new InvalidDataException("Unsupported terrain resolution.");
        for (var x = 0; x <= nodeUnits; x++)
        for (var y = 0; y <= nodeUnits; y++)
        {
            var sx = Math.Min(x / step, size - 2);
            var sy = Math.Min(y / step, size - 2);
            var fx = x / (float)step - sx;
            var fy = y / (float)step - sy;
            var h00 = HeightBits(sx, sy);
            var h10 = HeightBits(sx + 1, sy);
            var h01 = HeightBits(sx, sy + 1);
            var h11 = HeightBits(sx + 1, sy + 1);
            // Native interpolation precedes the float range and offset conversion.
            var height = offset + (((h10 * fx + h00 * (1 - fx)) * (1 - fy) +
                h01 * (1 - fx) * fy) + h11 * fx * fy) * range;
            if (!Finite(height))
                throw new InvalidDataException("Non-finite terrain height.");
            var index = (x0 + x) * GridSize + y0 + y;
            // A vertex belongs to the sector on its positive side. Retain outer border samples.
            if (x < nodeUnits && y < nodeUnits || float.IsNaN(_heights[index]))
                _heights[index] = height;
            if (x < nodeUnits && y < nodeUnits)
                _surfaces[SurfaceIndex(x0 + x, y0 + y)] = (byte)(packed[sx * size + sy] & 0x1f);
        }
        return;

        float HeightBits(int x, int y) => packed[x * size + y] & 0xffe0;
    }

    private bool TryLocate(float x, float y, out int cellX, out int cellY, out float fx, out float fy)
    {
        cellX = cellY = 0;
        fx = fy = 0;
        if (!Finite(x) || !Finite(y) || x < Bounds.Min.X || y < Bounds.Min.Y ||
            x > Bounds.Max.X || y > Bounds.Max.Y)
            return false;
        cellX = Cell(x, Origin.X);
        cellY = Cell(y, Origin.Y);
        fx = (x - Origin.X) / UnitSize - cellX;
        fy = (y - Origin.Y) / UnitSize - cellY;
        return true;
    }

    private int Cell(float position, float origin) => Math.Clamp((int)MathF.Floor((position - origin) / UnitSize), 0, GridSize - 2);
    private int SurfaceIndex(int x, int y) => x * (GridSize - 1) + y;
    private float Height(int x, int y) => _heights[x * GridSize + y];
    private static bool Finite(float value) => float.IsFinite(value);
    private static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static int ExactUnits(float metres, float unitSize, bool allowZero = false)
    {
        var value = metres / unitSize;
        if (!Finite(value) || value < (allowZero ? 0 : 1) || value > 4096 || value != MathF.Floor(value))
            throw new InvalidDataException("Terrain bounds do not match the unit grid.");
        return (int)value;
    }

    private static void Skip(BinaryReader reader, int count, long end)
    {
        if (count > end - reader.BaseStream.Position)
            throw new EndOfStreamException("Truncated terrain node data.");
        reader.BaseStream.Seek(count, SeekOrigin.Current);
    }

    private static bool Clip(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max,
        float limit, out float enter, out float leave)
    {
        enter = 0;
        leave = limit;
        for (var axis = 0; axis < 3; axis++)
        {
            if (direction[axis] == 0)
            {
                if (origin[axis] < min[axis] || origin[axis] > max[axis])
                    return false;
                continue;
            }
            var a = (min[axis] - origin[axis]) / direction[axis];
            var b = (max[axis] - origin[axis]) / direction[axis];
            enter = MathF.Max(enter, MathF.Min(a, b));
            leave = MathF.Min(leave, MathF.Max(a, b));
            if (enter > leave)
                return false;
        }
        return true;
    }

    private static bool IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        float limit, out float distance, out Vector3 normal)
    {
        distance = 0;
        var edge1 = b - a;
        var edge2 = c - a;
        normal = Vector3.Cross(edge1, edge2);
        var denominator = Vector3.Dot(direction, normal);
        if (denominator >= 0)
            return false;
        distance = Vector3.Dot(a - origin, normal) / denominator;
        if (distance < 0 || distance > limit)
            return false;
        var position = origin + direction * distance;
        // XY barycentrics avoid an arbitrary ray epsilon and preserve the native fixed diagonal.
        var x = (position.X - a.X) / (b.X != a.X ? b.X - a.X : c.X - a.X);
        var y = (position.Y - a.Y) / (c.Y != a.Y ? c.Y - a.Y : b.Y - a.Y);
        var lower = b.Y == a.Y;
        if (lower ? x < 0 || y < 0 || x + y > 1 : x < 0 || y < 0 || y < x || y > 1)
            return false;
        normal = Vector3.Normalize(normal);
        return true;
    }
}
