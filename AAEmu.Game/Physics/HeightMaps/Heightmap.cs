using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World;
using Jitter2.LinearMath;

namespace AAEmu.Game.Physics.HeightMaps;

public sealed class Heightmap(WorldTemplate world)
{
    public const int Spacing = WorldManager.CELL_SIZE / WorldManager.CELL_HMAP_RESOLUTION;
    public int QuadsX { get; } = checked(world.CellX * WorldManager.CELL_HMAP_RESOLUTION);
    public int QuadsZ { get; } = checked(world.CellY * WorldManager.CELL_HMAP_RESOLUTION);

    internal int TriangleCount => checked(QuadsX * QuadsZ * 2);
    internal int FirstTriangleIndex(int x, int z) => 2 * (x * QuadsZ + z);

    // Each completed cell publishes immutable unsigned samples in this range. Register the
    // envelope once so a cell that loads during a physics step needs no dynamic-tree mutation.
    public JBoundingBox GetBoundingBox() => new(JVector.Zero, new JVector(QuadsX * Spacing,
        (float)(ushort.MaxValue / world.HeightMaxCoefficient), QuadsZ * Spacing));

    public bool TryGetSample(int x, int z, out float height)
    {
        height = 0;
        if (QuadsX <= 0 || QuadsZ <= 0 || (uint)x > (uint)QuadsX || (uint)z > (uint)QuadsZ)
            return false;

        const int samples = WorldManager.CELL_HMAP_RESOLUTION;
        var cellX = Math.Min(x / samples, world.CellX - 1);
        var cellZ = Math.Min(z / samples, world.CellY - 1);
        if (Read(cellX, cellZ, out height))
            return true;

        // A vertex on a shared edge can also belong to the cell on its other side.
        var west = x == cellX * samples && cellX > 0;
        var south = z == cellZ * samples && cellZ > 0;
        return (west && Read(cellX - 1, cellZ, out height)) ||
               (south && Read(cellX, cellZ - 1, out height)) ||
               (west && south && Read(cellX - 1, cellZ - 1, out height));

        bool Read(int cx, int cz, out float value)
        {
            value = 0;
            return world.Cells[cx, cz]?.TryGetHeightMapSample(x - cx * samples, z - cz * samples, out value) == true;
        }
    }

    // Keep the existing canonical sample when two published cells disagree at a seam.
    // The square's own endpoint completes an edge whose neighboring cell is unavailable.
    public bool TryGetTriangle(int x, int z, bool upper, out CollisionTriangle triangle)
    {
        triangle = default;
        if ((uint)x >= (uint)QuadsX || (uint)z >= (uint)QuadsZ)
            return false;

        const int samples = WorldManager.CELL_HMAP_RESOLUTION;
        var cell = world.Cells[x / samples, z / samples];
        if (cell is not { Loaded: true, HeightMap: not null })
            return false;

        var localX = x % samples;
        var localZ = z % samples;
        bool ReadVertex(int dx, int dz, out float height)
        {
            var sx = localX + dx;
            var sz = localZ + dz;
            if ((sx == samples || sz == samples) && x + dx < QuadsX && z + dz < QuadsZ)
            {
                var neighbor = world.Cells[(x + dx) / samples, (z + dz) / samples];
                if (neighbor?.TryGetHeightMapSample(sx % samples, sz % samples, out height) == true)
                    return true;
            }
            return cell.TryGetHeightMapSample(sx, sz, out height);
        }

        if (!ReadVertex(1, 0, out var east) || !ReadVertex(0, 1, out var north) ||
            !ReadVertex(upper ? 1 : 0, upper ? 1 : 0, out var corner))
            return false;

        var left = x * Spacing;
        var low = z * Spacing;
        var right = left + Spacing;
        var high = low + Spacing;
        var eastPoint = new JVector(right, east, low);
        var northPoint = new JVector(left, north, high);
        if (upper)
        {
            triangle.A = new JVector(right, corner, high);
            triangle.B = eastPoint;
            triangle.C = northPoint;
        }
        else
        {
            triangle.A = new JVector(left, corner, low);
            triangle.B = northPoint;
            triangle.C = eastPoint;
        }
        return true;
    }
}
