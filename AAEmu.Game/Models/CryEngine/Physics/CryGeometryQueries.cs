using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Queries authored geometry. Numerical failure returns Indeterminate, never a clear placement.</summary>
public static class CryGeometryQueries
{
    public static bool Raycast(CryGeometryPart part, Matrix4x4 objectTransform, Vector3 origin,
        Vector3 direction, float maximumDistance, bool ignoreBackFaces, out CryRayHit hit)
    {
        hit = default;
        if (!float.IsFinite(maximumDistance) || maximumDistance < 0 ||
            MathF.Abs(direction.LengthSquared() - 1) > 0.001f)
            throw new ArgumentException("Ray needs a finite length and a unit direction.");
        var transform = part.Transform * objectTransform;
        if (!Matrix4x4.Invert(transform, out var inverse))
            throw new InvalidDataException("Singular collision transform.");
        var localOrigin = Vector3.Transform(origin, inverse);
        var localDirection = Vector3.TransformNormal(direction, inverse);
        var distance = maximumDistance;
        var normal = Vector3.Zero;
        var triangle = -1;
        var material = part.Shape.SurfaceIndex;
        var found = false;

        void Accept(float candidate, Vector3 candidateNormal, int candidateTriangle = -1, int candidateMaterial = -1)
        {
            if (candidate < 0 || candidate > distance || !float.IsFinite(candidate) ||
                (ignoreBackFaces && Vector3.Dot(candidateNormal, localDirection) >= 0))
                return;
            distance = candidate;
            normal = candidateNormal;
            triangle = candidateTriangle;
            material = candidateMaterial < 0 ? part.Shape.SurfaceIndex : candidateMaterial;
            found = true;
        }

        switch (part.Shape)
        {
            case CryBox box:
            {
                var boxTransform = BoxTransform(box);
                if (!Matrix4x4.Invert(boxTransform, out var boxInverse))
                    throw new InvalidDataException("Singular collision box.");
                var boxOrigin = Vector3.Transform(localOrigin, boxInverse);
                var boxDirection = Vector3.TransformNormal(localDirection, boxInverse);
                if (RayBox(boxOrigin, boxDirection, box.HalfSize, out var enter, out var exit,
                    out var enterNormal, out var exitNormal))
                {
                    Accept(enter, Vector3.TransformNormal(enterNormal, boxTransform));
                    Accept(exit, Vector3.TransformNormal(exitNormal, boxTransform));
                }
                break;
            }
            case CrySphere sphere:
                RaySphere(localOrigin, localDirection, sphere.Center, sphere.Radius, (candidate, candidateNormal) => Accept(candidate, candidateNormal));
                break;
            case CryCylinder cylinder:
                RayCylinder(localOrigin, localDirection, cylinder, (candidate, candidateNormal) => Accept(candidate, candidateNormal));
                break;
            case CryTriangleMesh mesh:
                for (var i = 0; i < mesh.Indices.Length; i += 3)
                {
                    var a = mesh.Vertices[mesh.Indices[i]];
                    var b = mesh.Vertices[mesh.Indices[i + 1]];
                    var c = mesh.Vertices[mesh.Indices[i + 2]];
                    if (RayTriangle(localOrigin, localDirection, a, b, c, out var candidate, out var candidateNormal))
                        Accept(candidate, candidateNormal, i / 3, mesh.MaterialIds.Length == 0 ? mesh.SurfaceIndex : mesh.MaterialIds[i / 3]);
                }
                break;
            default:
                throw new NotSupportedException("Unsupported collision shape.");
        }
        if (!found)
            return false;
        normal = Vector3.Normalize(Vector3.TransformNormal(normal, Matrix4x4.Transpose(inverse)));
        hit = new CryRayHit(distance, origin + direction * distance, normal, material, triangle);
        return true;
    }

    public static CryIntersection IntersectBox(CryGeometryPart part, Matrix4x4 objectTransform,
        CryBox box, Matrix4x4 boxTransform)
    {
        var worldBox = BoxTransform(box) * boxTransform;
        if (!Matrix4x4.Invert(worldBox, out var inverse))
            return CryIntersection.Indeterminate;
        var relative = part.Transform * objectTransform * inverse;
        if (!IsFinite(relative))
            return CryIntersection.Indeterminate;
        if (part.Shape is CryTriangleMesh mesh)
        {
            for (var i = 0; i < mesh.Indices.Length; i += 3)
            {
                var a = Vector3.Transform(mesh.Vertices[mesh.Indices[i]], relative);
                var b = Vector3.Transform(mesh.Vertices[mesh.Indices[i + 1]], relative);
                var c = Vector3.Transform(mesh.Vertices[mesh.Indices[i + 2]], relative);
                if (TriangleBox(a, b, c, box.HalfSize))
                    return CryIntersection.Intersects;
            }
            return CryIntersection.Clear;
        }
        if (part.Shape is CryBox other)
            return BoxBox(BoxTransform(other) * relative, other.HalfSize, box.HalfSize)
                ? CryIntersection.Intersects : CryIntersection.Clear;
        if (part.Shape is CrySphere sphere && TryUniformScale(relative, out var scale))
        {
            var center = Vector3.Transform(sphere.Center, relative);
            var closest = Vector3.Clamp(center, -box.HalfSize, box.HalfSize);
            var radius = sphere.Radius * scale;
            return Vector3.DistanceSquared(center, closest) <= radius * radius
                ? CryIntersection.Intersects : CryIntersection.Clear;
        }
        return ConvexBox(part.Shape, relative, box.HalfSize);
    }

    public static CryBounds GetBounds(CryGeometryPart part, Matrix4x4 objectTransform)
    {
        var transform = part.Transform * objectTransform;
        if (part.Shape is CryTriangleMesh mesh)
        {
            if (mesh.Indices.Length == 0)
                return new CryBounds(transform.Translation, transform.Translation);
            var first = Vector3.Transform(mesh.Vertices[mesh.Indices[0]], transform);
            var min = first;
            var max = first;
            foreach (var index in mesh.Indices)
            {
                var vertex = Vector3.Transform(mesh.Vertices[index], transform);
                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }
            return new CryBounds(min, max);
        }
        return new CryBounds(new Vector3(Support(part.Shape, transform, -Vector3.UnitX).X,
                Support(part.Shape, transform, -Vector3.UnitY).Y, Support(part.Shape, transform, -Vector3.UnitZ).Z),
            new Vector3(Support(part.Shape, transform, Vector3.UnitX).X,
                Support(part.Shape, transform, Vector3.UnitY).Y, Support(part.Shape, transform, Vector3.UnitZ).Z));
    }

    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 half, out float enter,
        out float exit, out Vector3 enterNormal, out Vector3 exitNormal)
    {
        enter = float.NegativeInfinity;
        exit = float.PositiveInfinity;
        enterNormal = exitNormal = Vector3.Zero;
        for (var axis = 0; axis < 3; axis++)
        {
            var p = Component(origin, axis);
            var d = Component(direction, axis);
            var h = Component(half, axis);
            if (d == 0)
            {
                if (p < -h || p > h)
                    return false;
                continue;
            }
            var first = (-h - p) / d;
            var second = (h - p) / d;
            var firstNormal = -Axis(axis);
            var secondNormal = Axis(axis);
            if (first > second)
            {
                (first, second) = (second, first);
                (firstNormal, secondNormal) = (secondNormal, firstNormal);
            }
            if (first > enter)
            {
                enter = first;
                enterNormal = firstNormal;
            }
            if (second < exit)
            {
                exit = second;
                exitNormal = secondNormal;
            }
            if (enter > exit)
                return false;
        }
        return exit >= 0;
    }

    private static void RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radius,
        Action<float, Vector3> accept)
    {
        var offset = origin - center;
        if (!Quadratic(Vector3.Dot(direction, direction), 2 * Vector3.Dot(offset, direction),
            Vector3.Dot(offset, offset) - radius * radius, out var first, out var second))
            return;
        accept(first, origin + direction * first - center);
        accept(second, origin + direction * second - center);
    }

    private static void RayCylinder(Vector3 origin, Vector3 direction, CryCylinder cylinder,
        Action<float, Vector3> accept)
    {
        var offset = origin - cylinder.Center;
        var axialOrigin = Vector3.Dot(offset, cylinder.Axis);
        var axialDirection = Vector3.Dot(direction, cylinder.Axis);
        var radialOrigin = offset - cylinder.Axis * axialOrigin;
        var radialDirection = direction - cylinder.Axis * axialDirection;
        if (Quadratic(radialDirection.LengthSquared(), 2 * Vector3.Dot(radialOrigin, radialDirection),
            radialOrigin.LengthSquared() - cylinder.Radius * cylinder.Radius, out var first, out var second))
        {
            foreach (var candidate in new[] { first, second })
            {
                var height = axialOrigin + candidate * axialDirection;
                if (MathF.Abs(height) <= cylinder.HalfHeight)
                    accept(candidate, radialOrigin + radialDirection * candidate);
            }
        }
        foreach (var sign in new[] { -1f, 1f })
        {
            if (cylinder.IsCapsule)
            {
                var center = cylinder.Center + cylinder.Axis * (sign * cylinder.HalfHeight);
                RaySphere(origin, direction, center, cylinder.Radius, (candidate, normal) =>
                {
                    if (sign * Vector3.Dot(normal, cylinder.Axis) >= 0)
                        accept(candidate, normal);
                });
            }
            else if (axialDirection != 0)
            {
                var candidate = (sign * cylinder.HalfHeight - axialOrigin) / axialDirection;
                if ((radialOrigin + radialDirection * candidate).LengthSquared() <= cylinder.Radius * cylinder.Radius)
                    accept(candidate, cylinder.Axis * sign);
            }
        }
    }

    private static bool Quadratic(float a, float b, float c, out float first, out float second)
    {
        first = second = 0;
        if (a == 0)
            return false;
        var discriminant = (double)b * b - 4d * a * c;
        if (discriminant < 0)
            return false;
        var root = Math.Sqrt(discriminant);
        first = (float)((-b - root) / (2d * a));
        second = (float)((-b + root) / (2d * a));
        return true;
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        out float distance, out Vector3 normal)
    {
        distance = 0;
        var edge1 = b - a;
        var edge2 = c - a;
        normal = Vector3.Cross(edge1, edge2);
        var cross = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, cross);
        if (determinant == 0)
            return false;
        var inverse = 1d / determinant;
        var offset = origin - a;
        var u = Vector3.Dot(offset, cross) * inverse;
        if (u < 0 || u > 1)
            return false;
        cross = Vector3.Cross(offset, edge1);
        var v = Vector3.Dot(direction, cross) * inverse;
        if (v < 0 || u + v > 1)
            return false;
        distance = (float)(Vector3.Dot(edge2, cross) * inverse);
        return distance >= 0;
    }

    private static bool TriangleBox(Vector3 a, Vector3 b, Vector3 c, Vector3 half)
    {
        var edges = new[] { b - a, c - b, a - c };
        if (Separates(Vector3.UnitX) || Separates(Vector3.UnitY) || Separates(Vector3.UnitZ) ||
            Separates(Vector3.Cross(edges[0], edges[1])))
            return false;
        foreach (var edge in edges)
            for (var axis = 0; axis < 3; axis++)
                if (Separates(Vector3.Cross(edge, Axis(axis))))
                    return false;
        return true;

        bool Separates(Vector3 axis)
        {
            var pa = Vector3.Dot(a, axis);
            var pb = Vector3.Dot(b, axis);
            var pc = Vector3.Dot(c, axis);
            var radius = Vector3.Dot(Vector3.Abs(axis), half);
            return MathF.Min(pa, MathF.Min(pb, pc)) > radius || MathF.Max(pa, MathF.Max(pb, pc)) < -radius;
        }
    }

    private static bool BoxBox(Matrix4x4 transform, Vector3 otherHalf, Vector3 half)
    {
        var center = transform.Translation;
        var edges = new[] { Vector3.TransformNormal(Vector3.UnitX, transform),
            Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.TransformNormal(Vector3.UnitZ, transform) };
        for (var axis = 0; axis < 3; axis++)
        {
            if (Separates(Axis(axis)) || Separates(Vector3.Cross(edges[(axis + 1) % 3], edges[(axis + 2) % 3])))
                return false;
            foreach (var edge in edges)
                if (Separates(Vector3.Cross(edge, Axis(axis))))
                    return false;
        }
        return true;

        bool Separates(Vector3 axis)
        {
            var radius = Vector3.Dot(Vector3.Abs(axis), half) + MathF.Abs(Vector3.Dot(axis, edges[0])) * otherHalf.X +
                MathF.Abs(Vector3.Dot(axis, edges[1])) * otherHalf.Y + MathF.Abs(Vector3.Dot(axis, edges[2])) * otherHalf.Z;
            return MathF.Abs(Vector3.Dot(center, axis)) > radius;
        }
    }

    private static CryIntersection ConvexBox(CryPhysicsShape shape, Matrix4x4 transform, Vector3 half)
    {
        // GJK tests the exact support maps of the curved primitive and the query box.
        // It does not tessellate cylinders, capsules, or spheres.
        Vector3 Difference(Vector3 direction) => Support(shape, transform, direction) +
            new Vector3(direction.X >= 0 ? half.X : -half.X, direction.Y >= 0 ? half.Y : -half.Y,
                direction.Z >= 0 ? half.Z : -half.Z);
        var direction = Vector3.UnitX;
        var simplex = new List<Vector3>(4) { Difference(direction) };
        direction = -simplex[0];
        for (var iteration = 0; iteration < 96; iteration++)
        {
            if (direction.LengthSquared() == 0)
                return CryIntersection.Intersects;
            var point = Difference(direction);
            if (Vector3.Dot(point, direction) < 0)
                return CryIntersection.Clear;
            if (simplex.Contains(point))
                return CryIntersection.Indeterminate;
            simplex.Insert(0, point);
            if (UpdateSimplex(simplex, ref direction))
                return CryIntersection.Intersects;
        }
        return CryIntersection.Indeterminate;
    }

    private static bool UpdateSimplex(List<Vector3> points, ref Vector3 direction)
    {
        var a = points[0];
        var ao = -a;
        var ab = points[1] - a;
        if (points.Count == 2)
        {
            if (Vector3.Dot(ab, ao) > 0)
                direction = Vector3.Cross(Vector3.Cross(ab, ao), ab);
            else
            {
                points.RemoveAt(1);
                direction = ao;
            }
            return direction.LengthSquared() == 0;
        }
        var ac = points[2] - a;
        var abc = Vector3.Cross(ab, ac);
        if (points.Count == 3)
        {
            if (Vector3.Dot(Vector3.Cross(abc, ac), ao) > 0)
            {
                if (Vector3.Dot(ac, ao) > 0)
                {
                    points.RemoveAt(1);
                    direction = Vector3.Cross(Vector3.Cross(ac, ao), ac);
                    return direction.LengthSquared() == 0;
                }
                points.RemoveAt(2);
                return UpdateSimplex(points, ref direction);
            }
            if (Vector3.Dot(Vector3.Cross(ab, abc), ao) > 0)
            {
                points.RemoveAt(2);
                return UpdateSimplex(points, ref direction);
            }
            if (Vector3.Dot(abc, ao) > 0)
                direction = abc;
            else
            {
                (points[1], points[2]) = (points[2], points[1]);
                direction = -abc;
            }
            return direction.LengthSquared() == 0;
        }
        var ad = points[3] - a;
        var faces = new[] { (1, 2, abc), (2, 3, Vector3.Cross(ac, ad)), (3, 1, Vector3.Cross(ad, ab)) };
        foreach (var (first, second, normal) in faces)
        {
            if (Vector3.Dot(normal, ao) <= 0)
                continue;
            var b = points[first];
            var c = points[second];
            points.Clear();
            points.AddRange([a, b, c]);
            return UpdateSimplex(points, ref direction);
        }
        return true;
    }

    private static Vector3 Support(CryPhysicsShape shape, Matrix4x4 transform, Vector3 direction)
    {
        var localDirection = Vector3.TransformNormal(direction, Matrix4x4.Transpose(transform));
        Vector3 local;
        switch (shape)
        {
            case CryBox box:
                var axes = BoxTransform(box);
                var boxDirection = Vector3.TransformNormal(localDirection, Matrix4x4.Transpose(axes));
                local = Vector3.Transform(new Vector3(boxDirection.X >= 0 ? box.HalfSize.X : -box.HalfSize.X,
                    boxDirection.Y >= 0 ? box.HalfSize.Y : -box.HalfSize.Y,
                    boxDirection.Z >= 0 ? box.HalfSize.Z : -box.HalfSize.Z), axes);
                break;
            case CrySphere sphere:
                local = sphere.Center + NormalizeOrZero(localDirection) * sphere.Radius;
                break;
            case CryCylinder cylinder:
                var axial = Vector3.Dot(localDirection, cylinder.Axis);
                var radial = localDirection - cylinder.Axis * axial;
                local = cylinder.Center + cylinder.Axis * (axial >= 0 ? cylinder.HalfHeight : -cylinder.HalfHeight);
                local += NormalizeOrZero(cylinder.IsCapsule ? localDirection : radial) * cylinder.Radius;
                break;
            default:
                throw new NotSupportedException("Shape has no convex support map.");
        }
        return Vector3.Transform(local, transform);
    }

    private static Matrix4x4 BoxTransform(CryBox box) => box.Orientation * Matrix4x4.CreateTranslation(box.Center);
    private static Vector3 NormalizeOrZero(Vector3 value) => value.LengthSquared() == 0 ? Vector3.Zero : Vector3.Normalize(value);
    private static Vector3 Axis(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
    private static float Component(Vector3 value, int axis) => axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };

    private static bool TryUniformScale(Matrix4x4 matrix, out float scale)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, matrix);
        var y = Vector3.TransformNormal(Vector3.UnitY, matrix);
        var z = Vector3.TransformNormal(Vector3.UnitZ, matrix);
        var square = x.LengthSquared();
        scale = MathF.Sqrt(square);
        var tolerance = square * 1e-6f;
        return square > 0 && MathF.Abs(y.LengthSquared() - square) <= tolerance &&
            MathF.Abs(z.LengthSquared() - square) <= tolerance && MathF.Abs(Vector3.Dot(x, y)) <= tolerance &&
            MathF.Abs(Vector3.Dot(x, z)) <= tolerance && MathF.Abs(Vector3.Dot(y, z)) <= tolerance;
    }

    private static bool IsFinite(Matrix4x4 matrix) => float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M41) &&
        float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43);
}
