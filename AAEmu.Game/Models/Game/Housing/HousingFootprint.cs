using System.Numerics;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// The axis-aligned r208022 garden footprint. This is not the house model bounds.
/// See Docs/customized/Housing-Geometry-307-309.md for the native contract.
/// </summary>
public readonly record struct HousingFootprint(float MinX, float MinY, float MaxX, float MaxY)
{
    public static bool TryCreateGarden(Vector2 position, float gardenRadius, float alley,
        out HousingFootprint footprint)
    {
        footprint = default;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(gardenRadius) || gardenRadius <= 0 ||
            !float.IsFinite(alley) || alley < 0)
        {
            return false;
        }

        // 39897170: the native garden uses 4 m cells and a maximum of 11 cells.
        var cellCountValue = (gardenRadius * 2f + 3f) * 0.25f;
        if (!float.IsFinite(cellCountValue) || cellCountValue < 1 || cellCountValue >= 12)
            return false;
        var cellCount = (int)cellCountValue;
        var size = cellCount * 4;
        if (alley * 2 >= size)
            return false;

        // 39326980 uses integer conversion toward zero, not MathF.Floor.
        var cellX = ((position.X - size / 2) + 2f) * 0.25f;
        var cellY = ((position.Y - size / 2) + 2f) * 0.25f;
        if (cellX < int.MinValue / 4 || cellX >= int.MaxValue / 4 ||
            cellY < int.MinValue / 4 || cellY >= int.MaxValue / 4)
        {
            return false;
        }
        var minX = (int)cellX * 4;
        var minY = (int)cellY * 4;
        // 39326ab0 reserves the alley on all four sides for garden placement.
        footprint = new HousingFootprint(minX + alley, minY + alley,
            minX + size - alley, minY + size - alley);
        return true;
    }

    public bool Contains(float x, float y)
    {
        // 39326b40 includes the minimum edges and excludes the maximum edges.
        return float.IsFinite(x) && float.IsFinite(y) &&
            MinX <= x && x < MaxX && MinY <= y && y < MaxY;
    }

    public bool Contains(Vector2 position)
    {
        return Contains(position.X, position.Y);
    }
}
