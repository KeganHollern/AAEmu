using System.Numerics;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// A static client waterfall brush. This is intentionally not a <see cref="WaterBodyArea"/>:
/// the mesh marks a falling-water transition corridor, not a horizontal buoyancy surface.
/// </summary>
public sealed class WaterfallArea
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string AssetPath { get; init; } = string.Empty;
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }

    public bool Intersects(float minX, float minY, float minZ, float maxX, float maxY, float maxZ) =>
        Max.X >= minX && Min.X <= maxX &&
        Max.Y >= minY && Min.Y <= maxY &&
        Max.Z >= minZ && Min.Z <= maxZ;
}
