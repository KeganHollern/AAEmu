using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World;
using Jitter2.LinearMath;

namespace AAEmu.Game.Physics.HeightMaps;

public sealed class Heightmap(WorldTemplate world)
{
    public const int Spacing = WorldManager.CELL_SIZE / WorldManager.CELL_HMAP_RESOLUTION;
    public int QuadsX { get; } = checked(world.CellX * WorldManager.CELL_HMAP_RESOLUTION - 1);
    public int QuadsZ { get; } = checked(world.CellY * WorldManager.CELL_HMAP_RESOLUTION - 1);

    internal int TriangleCount => checked(QuadsX * QuadsZ * 2);
    internal int FirstTriangleIndex(int x, int z) => 2 * (x * QuadsZ + z);

    // Each completed cell publishes immutable unsigned samples in this range. Register the
    // envelope once so a cell that loads during a physics step needs no dynamic-tree mutation.
    public JBoundingBox GetBoundingBox() => new(JVector.Zero, new JVector(QuadsX * Spacing,
        (float)(ushort.MaxValue / world.HeightMaxCoefficient), QuadsZ * Spacing));

    public bool TryGetSample(int x, int z, out float height)
    {
        height = 0;
        if ((uint)x > (uint)QuadsX || (uint)z > (uint)QuadsZ)
            return false;

        const int samples = WorldManager.CELL_HMAP_RESOLUTION;
        var cell = world.Cells[x / samples, z / samples];
        if (cell is null || !cell.Loaded || cell.HeightMap is null)
            return false;

        height = (float)(cell.HeightMap[x % samples, z % samples] / world.HeightMaxCoefficient);
        return true;
    }

    // CryEngine splits each square from (x+1,z) to (x,z+1). Each half needs only
    // its own three measured samples, including where two loaded cells meet.
    public bool TryGetTriangle(int x, int z, bool upper, out CollisionTriangle triangle)
    {
        triangle = default;
        if (!TryGetSample(x + 1, z, out var east) || !TryGetSample(x, z + 1, out var north) ||
            !TryGetSample(upper ? x + 1 : x, upper ? z + 1 : z, out var corner))
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
