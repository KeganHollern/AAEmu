using System.Numerics;

namespace AAEmu.Game.Models.Game.Shipyard;

public sealed class ShipyardReward
{
    public uint Id { get; init; }
    public uint DoodadId { get; init; }
    public bool OnWater { get; init; }
    public float Radius { get; init; }
    public int Count { get; init; }

    internal Vector3 GetPosition(Vector3 origin, Random random, Func<Vector3, bool, float> getHeight)
    {
        // Polar coordinates avoid normalizing a zero vector. The square root
        // distributes debris across the disk rather than concentrating its center.
        var angle = random.NextDouble() * Math.Tau;
        var distance = Math.Sqrt(random.NextDouble()) * Radius;
        var position = origin + new Vector3((float)(Math.Cos(angle) * distance),
            (float)(Math.Sin(angle) * distance), 0);
        position.Z = getHeight(position, OnWater);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new InvalidOperationException($"Shipyard reward {Id} has an invalid position.");
        return position;
    }
}
