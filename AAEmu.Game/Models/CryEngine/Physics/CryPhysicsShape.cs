using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

public readonly record struct CryBounds(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 HalfSize => (Max - Min) * 0.5f;

    public bool Contains(Vector3 point) => point.X >= Min.X && point.Y >= Min.Y && point.Z >= Min.Z &&
        point.X <= Max.X && point.Y <= Max.Y && point.Z <= Max.Z;

    public bool Intersects(CryBounds other) => Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public CryBounds Transform(Matrix4x4 transform)
    {
        var center = Vector3.Transform(Center, transform);
        var half = HalfSize;
        var extent = new Vector3(
            MathF.Abs(transform.M11) * half.X + MathF.Abs(transform.M21) * half.Y + MathF.Abs(transform.M31) * half.Z,
            MathF.Abs(transform.M12) * half.X + MathF.Abs(transform.M22) * half.Y + MathF.Abs(transform.M32) * half.Z,
            MathF.Abs(transform.M13) * half.X + MathF.Abs(transform.M23) * half.Y + MathF.Abs(transform.M33) * half.Z);
        return new CryBounds(center - extent, center + extent);
    }

    public CryBounds Union(CryBounds other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
}

public abstract record CryPhysicsShape(int SurfaceIndex);

// Orientation maps box coordinates to shape coordinates. CryPhysics serializes its transpose.
public sealed record CryBox(Vector3 Center, Vector3 HalfSize, Matrix4x4 Orientation, int SurfaceIndex = 0)
    : CryPhysicsShape(SurfaceIndex);

public sealed record CrySphere(Vector3 Center, float Radius, int SurfaceIndex = 0) : CryPhysicsShape(SurfaceIndex);

public sealed record CryCylinder(Vector3 Center, Vector3 Axis, float Radius, float HalfHeight,
    bool IsCapsule, int SurfaceIndex = 0) : CryPhysicsShape(SurfaceIndex);

public sealed record CryTriangleMesh(Vector3[] Vertices, ushort[] Indices, byte[] MaterialIds,
    int SurfaceIndex = 0) : CryPhysicsShape(SurfaceIndex);

public sealed record CryGeometryPart(CryPhysicsShape Shape, Matrix4x4 Transform, int PhysicsType,
    string MaterialPath, string NodeName)
{
    public string PhysicsGroup { get; init; } = "";
    public int SpineCount { get; init; }
    public int PickingIndex { get; init; }
}

public sealed record CryGeometryAsset(CryBounds Bounds, IReadOnlyList<CryGeometryPart> Parts)
{
    public IReadOnlyList<CryGeometryHelper> Helpers { get; init; } = [];
    public IReadOnlyList<CryGeometryPoseRequirement> PoseRequirements { get; init; } = [];
    public bool HasAnimatedCollision { get; init; }
}

public sealed record CryGeometryHelper(string Name, string Text, Matrix4x4 Transform);

public sealed record CryGeometryPoseRequirement(string ModelUri, string Name, Matrix4x4 Transform,
    string Animation, bool Playing, bool Physicalized);

public readonly record struct CryRayHit(float Distance, Vector3 Position, Vector3 Normal, int MaterialId,
    int TriangleIndex);

public enum CryIntersection
{
    Clear,
    Intersects,
    Indeterminate
}
