using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>An authored WaterVolume child, before the prefab instance transform.</summary>
public sealed record CryPrefabWaterVolume(string Name, IReadOnlyList<Vector3> Points, Matrix4x4 Transform, float Depth)
{
    /// <summary>Native3910b600 transforms first, then projects vertically onto its transformed +Z plane.</summary>
    public Vector3[] GetPhysicsContour(Matrix4x4 parentTransform, float heightOffset, out Vector3 normal)
    {
        var transform = Transform * parentTransform;
        normal = Vector3.TransformNormal(Vector3.UnitZ, transform);
        if (!Finite(normal) || normal.LengthSquared() == 0 || !float.IsFinite(Depth) || !float.IsFinite(heightOffset))
            throw new InvalidDataException("Invalid prefab water transform or depth.");
        normal = Vector3.Normalize(normal);
        // Native300e85a0 requires the fog plane normal Z to be greater than 0.0001.
        if (Points.Count < 4 || normal.Z <= 0.0001f)
            return [];
        var points = Points.Select(point => Vector3.Transform(point, transform) + new Vector3(0, 0, heightOffset)).ToArray();
        if (points.Any(point => !Finite(point)))
            throw new InvalidDataException("Invalid prefab water points.");
        var d = -Vector3.Dot(normal, points[0]);
        for (var i = 0; i < points.Length; i++)
            points[i] = points[i] with { Z = points[i].Z - (Vector3.Dot(normal, points[i]) + d) / normal.Z };
        return points;
    }

    /// <summary>
    /// Native39103020 applies the child transform to render-node local bounds. The render node
    /// centers its projected world AABB, including the unscaled downward volume depth.
    /// </summary>
    public CryBounds? GetModelBounds(Matrix4x4 parentTransform, float heightOffset = 0)
    {
        var points = GetPhysicsContour(parentTransform, heightOffset, out _);
        if (points.Length == 0)
            return null;
        var minimum = points.Aggregate(Vector3.Min) - new Vector3(0, 0, Depth + heightOffset);
        var maximum = points.Aggregate(Vector3.Max);
        var half = (maximum - minimum) * 0.5f;
        return new CryBounds(-half, half).Transform(Transform);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>The caller supplies active instances in oldest-to-newest server registration order.</summary>
public sealed record CryWaterVolumeInstance(IReadOnlyList<CryPrefabWaterVolume> Volumes, Matrix4x4 Transform,
    float HeightOffset = 0);
