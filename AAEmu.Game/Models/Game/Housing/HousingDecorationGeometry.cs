using System.Numerics;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.Game.Housing;

public readonly record struct HousingDecorationRayResult(CryIntersection Intersection, bool IsTerrain);

/// <summary>The r208022 decoration predicates, independent of the world geometry provider.</summary>
public static class HousingDecorationGeometry
{
    public const float HouseSelectionMargin = 2f;
    public const float SurfaceThreshold = 0.5f;
    public const float CollisionHalfHeightInset = 0.025f;
    public const float InteriorRayExtraDepth = 10f;

    public static ErrorMessageType Check(HousingDecoration design, CryBounds localBounds,
        Matrix4x4 decorationToWorld, CryBox houseBounds, CryBounds gardenBounds, bool hasGarden,
        Vector3 supportNormal, bool supportAllowed,
        Func<Vector3, Vector3, HousingDecorationRayResult> traceInterior,
        Func<CryBox, CryIntersection> overlaps)
    {
        if (design == null || !supportAllowed || !IsSupportAllowed(design, supportNormal) ||
            !Valid(localBounds) || !Valid(gardenBounds) || !Finite(decorationToWorld) ||
            houseBounds == null || !Finite(houseBounds.Orientation) || !Finite(houseBounds.Center) || !Finite(houseBounds.HalfSize) ||
            Vector3.Min(houseBounds.HalfSize, Vector3.Zero) != Vector3.Zero ||
            !Matrix4x4.Invert(houseBounds.Orientation, out var inverseHouseOrientation))
            return ErrorMessageType.HouseCannotDecorateSurface;

        var pivot = decorationToWorld.Translation;
        if (hasGarden && !ContainsHalfOpen(gardenBounds, pivot))
            return ErrorMessageType.HouseCannotDecorateOutOfGarden;

        var corners = Corners(localBounds, decorationToWorld);
        var interior = true;
        // Native 0x107/0x8f ray starts at the garden top and ends 10 m below its bottom.
        var rayVector = new Vector3(0, 0, gardenBounds.Min.Z - gardenBounds.Max.Z - InteriorRayExtraDepth);
        foreach (var corner in corners)
        {
            var pointInHouse = Vector3.TransformNormal(corner - houseBounds.Center, inverseHouseOrientation);
            var distanceFromCenter = Vector3.Abs(pointInHouse);
            if (distanceFromCenter.X > houseBounds.HalfSize.X ||
                distanceFromCenter.Y > houseBounds.HalfSize.Y ||
                distanceFromCenter.Z > houseBounds.HalfSize.Z)
            {
                interior = false;
                break;
            }
            var hit = traceInterior(new Vector3(corner.X, corner.Y, gardenBounds.Max.Z), rayVector);
            if (hit.Intersection == CryIntersection.Indeterminate)
                return ErrorMessageType.HouseCannotDecorateSurface;
            if (hit.Intersection == CryIntersection.Intersects && hit.IsTerrain)
            {
                interior = false;
                break;
            }
        }
        if (interior)
        {
            if (!design.AllowOnCeiling && !design.AllowOnFloor && !design.AllowOnWall)
                return ErrorMessageType.HouseCannotDecorateInHouse;
        }
        else if (!design.AllowPivotOnGarden && !design.AllowMeshOnGarden && !design.AllowOnWall)
            return ErrorMessageType.HouseCannotDecorateOutOfHouse;

        if (design.AllowMeshOnGarden && corners.Any(corner => !ContainsHalfOpen(gardenBounds, corner)))
            return ErrorMessageType.HouseCannotDecorateOutOfGarden;

        if (TryCreateCollisionBox(localBounds, decorationToWorld, out var collisionBox) &&
            overlaps(collisionBox) != CryIntersection.Clear)
            return ErrorMessageType.HouseCannotDecorateOverlap;
        return ErrorMessageType.NoErrorMessage;
    }

    public static bool IsWithinSelectionRange(CryBounds gardenBounds, Vector3 playerPosition)
    {
        if (!Valid(gardenBounds) || !Finite(playerPosition))
            return false;
        var closest = Vector3.Clamp(playerPosition, gardenBounds.Min, gardenBounds.Max);
        return Vector3.DistanceSquared(playerPosition, closest) <= HouseSelectionMargin * HouseSelectionMargin;
    }

    public static bool IsSupportAllowed(HousingDecoration design, Vector3 normal)
    {
        if (design == null || !Finite(normal) || normal.LengthSquared() == 0)
            return false;
        if (normal.Z > SurfaceThreshold)
            return design.AllowOnFloor || design.AllowPivotOnGarden || design.AllowMeshOnGarden;
        if (normal.Z < -SurfaceThreshold)
            return design.AllowOnCeiling;
        return design.AllowOnWall;
    }

    public static bool TryCreateCollisionBox(CryBounds localBounds, Matrix4x4 decorationToWorld, out CryBox box)
    {
        box = null;
        if (!Valid(localBounds) || !Finite(decorationToWorld))
            return false;
        // The native box clips a negative local minimum to the pivot before its support inset.
        var clippedMin = localBounds.Min with { Z = MathF.Max(0, localBounds.Min.Z) };
        var center = (clippedMin + localBounds.Max) * 0.5f;
        var half = (localBounds.Max - clippedMin) * 0.5f;
        center.Z += CollisionHalfHeightInset;
        half.Z -= CollisionHalfHeightInset;
        if (half.Z <= 0)
            return false;
        var orientation = decorationToWorld;
        orientation.Translation = Vector3.Zero;
        box = new CryBox(Vector3.Transform(center, decorationToWorld), half, orientation);
        return true;
    }

    private static Vector3[] Corners(CryBounds bounds, Matrix4x4 transform)
    {
        var result = new Vector3[8];
        for (var i = 0; i < result.Length; i++)
            result[i] = Vector3.Transform(new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z), transform);
        return result;
    }

    private static bool ContainsHalfOpen(CryBounds bounds, Vector3 point) =>
        point.X >= bounds.Min.X && point.X < bounds.Max.X &&
        point.Y >= bounds.Min.Y && point.Y < bounds.Max.Y &&
        point.Z >= bounds.Min.Z && point.Z < bounds.Max.Z;

    private static bool Valid(CryBounds bounds) => Finite(bounds.Min) && Finite(bounds.Max) &&
        bounds.Min.X <= bounds.Max.X && bounds.Min.Y <= bounds.Max.Y && bounds.Min.Z <= bounds.Max.Z;

    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
