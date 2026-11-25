using Jitter2.Collision;
using Jitter2.LinearMath;

namespace AAEmu.Game.Physics.HeightMaps;

public sealed class HeightmapTester(Heightmap heightmap) : IDynamicTreeProxy, IRayCastable
{
    public int SetIndex { get; set; } = -1;
    public int NodePtr { get; set; }
    public JVector Velocity => JVector.Zero;
    public JBoundingBox WorldBoundingBox { get; } = heightmap.GetBoundingBox();
    public Heightmap Heightmap { get; } = heightmap;

    public bool RayCast(in JVector origin, in JVector direction, out JVector normal, out float lambda)
    {
        normal = JVector.Zero;
        lambda = 0;
        if (direction.LengthSquared() == 0 || !WorldBoundingBox.RayIntersect(origin, direction, out var entry))
            return false;

        const int spacing = Heightmap.Spacing;
        var start = origin + Math.Max(entry, 0) * direction;
        var x = Math.Clamp((int)MathF.Floor(start.X / spacing), 0, Heightmap.QuadsX - 1);
        var z = Math.Clamp((int)MathF.Floor(start.Z / spacing), 0, Heightmap.QuadsZ - 1);
        var stepX = Math.Sign(direction.X);
        var stepZ = Math.Sign(direction.Z);
        var deltaX = stepX == 0 ? float.PositiveInfinity : spacing / Math.Abs(direction.X);
        var deltaZ = stepZ == 0 ? float.PositiveInfinity : spacing / Math.Abs(direction.Z);
        var nextX = stepX == 0 ? float.PositiveInfinity : ((x + (stepX > 0 ? 1 : 0)) * spacing - origin.X) / direction.X;
        var nextZ = stepZ == 0 ? float.PositiveInfinity : ((z + (stepZ > 0 ? 1 : 0)) * spacing - origin.Z) / direction.Z;

        // Keep the original 100 m horizontal ray range. A vertical ray visits one square.
        var horizontalSpeed = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        var limit = horizontalSpeed == 0 ? float.PositiveInfinity : 100f / horizontalSpeed;
        var t = Math.Max(0f, entry);
        while (t <= limit && (uint)x < (uint)Heightmap.QuadsX && (uint)z < (uint)Heightmap.QuadsZ)
        {
            var end = Math.Min(limit, Math.Min(nextX, nextZ));
            // On a grid line the ray can touch a measured face on either side.
            var west = stepX == 0 && x > 0 && origin.X == x * spacing;
            var south = stepZ == 0 && z > 0 && origin.Z == z * spacing;
            if (IntersectSquare(x, z, origin, direction, end, out normal, out lambda) ||
                (west && IntersectSquare(x - 1, z, origin, direction, end, out normal, out lambda)) ||
                (south && IntersectSquare(x, z - 1, origin, direction, end, out normal, out lambda)) ||
                (west && south && IntersectSquare(x - 1, z - 1, origin, direction, end, out normal, out lambda)))
                return true;
            if (float.IsPositiveInfinity(end) || end >= limit)
                break;
            t = Math.Min(nextX, nextZ);
            if (nextX <= t)
            {
                x += stepX;
                nextX += deltaX;
            }
            if (nextZ <= t)
            {
                z += stepZ;
                nextZ += deltaZ;
            }
        }
        normal = JVector.Zero;
        lambda = 0;
        return false;
    }

    private bool IntersectSquare(int x, int z, in JVector origin, in JVector direction,
        float end, out JVector normal, out float lambda)
    {
        var lower = Intersect(x, z, false, origin, direction, end, out normal, out lambda);
        if (Intersect(x, z, true, origin, direction, end, out var otherNormal, out var otherLambda) &&
            (!lower || otherLambda < lambda))
        {
            normal = otherNormal;
            lambda = otherLambda;
            return true;
        }
        return lower;
    }

    private bool Intersect(int x, int z, bool upper, in JVector origin, in JVector direction,
        float end, out JVector normal, out float lambda)
    {
        normal = JVector.Zero;
        lambda = 0;
        if (!Heightmap.TryGetTriangle(x, z, upper, out var face))
            return false;
        // Include points on the shared diagonal and the outer edges of a measured face.
        normal = (face.B - face.A) % (face.C - face.A);
        var denominator = JVector.Dot(direction, normal);
        if (denominator >= 0f)
            return false;
        lambda = JVector.Dot(face.A - origin, normal) / denominator;
        if (lambda < 0f || lambda > end)
            return false;

        const int spacing = Heightmap.Spacing;
        var hit = origin + lambda * direction;
        var localX = hit.X - x * spacing;
        var localZ = hit.Z - z * spacing;
        if (localX < 0 || localX > spacing || localZ < 0 || localZ > spacing ||
            (upper ? localX + localZ < spacing : localX + localZ > spacing))
            return false;

        normal = JVector.Normalize(normal);
        return true;
    }
}
