using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.Game.Slaves;

/// <summary>The r208022 skill-driven vehicle placement checks, in world metres.</summary>
public static class SlavePlacementRules
{
    public const float MaximumDistance = 80;
    public const float MaximumLandRise = 10;
    // Float interpolation and packed XY introduce small surface differences at world coordinates.
    internal const float SurfaceTolerance = 0.01f;

    public static bool IsFinite(Vector3 position) => float.IsFinite(position.X) &&
        float.IsFinite(position.Y) && float.IsFinite(position.Z);

    public static bool ValidBounds(CryBounds bounds) => IsFinite(bounds.Min) && IsFinite(bounds.Max) &&
        bounds.Min.X < bounds.Max.X && bounds.Min.Y < bounds.Max.Y && bounds.Min.Z < bounds.Max.Z;

    public static bool IsWithinRange(Vector3 owner, Vector3 position) => IsFinite(owner) && IsFinite(position) &&
        Vector3.DistanceSquared(owner, position) <= MaximumDistance * MaximumDistance;

    public static bool IsWithinSavedLocationRange(Vector3 owner, Vector3 saved, uint range) =>
        IsFinite(owner) && IsFinite(saved) &&
        Vector2.DistanceSquared(new(owner.X, owner.Y), new(saved.X, saved.Y)) <= (double)range * range;

    public static bool IsOnSurface(Vector3 position, float height) => IsFinite(position) &&
        float.IsFinite(height) && MathF.Abs(position.Z - height) <= SurfaceTolerance;

    public static CryBox PlacementBox(CryBounds bounds, SlavePlacementRequest request) =>
        // Native39180d50 adds the local centre directly to the selected position for its narrow query.
        new(request.Position + bounds.Center, bounds.HalfSize, Matrix4x4.CreateRotationZ(request.Yaw));

    public static ErrorMessageType CheckGeometry(Vector3 owner, SlavePlacementRequest request,
        CryBounds bounds, bool boat, float terrain, float surface,
        Func<CryBox, int, CryIntersection> intersect, Func<Vector3, Vector3, CryIntersection> trace)
    {
        if (!IsWithinRange(owner, request.Position) || !float.IsFinite(request.Yaw) || !ValidBounds(bounds) ||
            !float.IsFinite(terrain) || !IsOnSurface(request.Position, surface) ||
            (boat ? terrain > surface : surface - owner.Z > MaximumLandRise))
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;

        var rayStart = owner + Vector3.UnitZ;
        if (trace(rayStart, request.Position) != CryIntersection.Clear)
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        return intersect(PlacementBox(bounds, request), boat ? 0x10f : 0xf) == CryIntersection.Clear
            ? ErrorMessageType.NoErrorMessage : ErrorMessageType.SlaveSpawnShipNeedMoreSpace;
    }
}
