using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.Game.Shipyard;

/// <summary>The r208022 shipyard preview volumes, in metres.</summary>
public static class ShipyardPlacementRules
{
    public const float MaximumDistance = 45;
    public const float PreviewHeight = 9.9f;
    private const float ForwardClearance = 15;
    private const float VerticalClearance = 1.3f;

    public static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    public static bool ValidBounds(CryBounds bounds) => IsFinite(bounds.Min) && IsFinite(bounds.Max) &&
        bounds.Min.X < bounds.Max.X && bounds.Min.Y < bounds.Max.Y && bounds.Min.Z < bounds.Max.Z;

    public static bool IsWithinRange(Vector3 owner, Vector3 position) => IsFinite(owner) && IsFinite(position) &&
        Vector3.DistanceSquared(owner, position) <= MaximumDistance * MaximumDistance;

    public static bool IsOnWater(Vector3 position, float surface)
    {
        // Native394d73e0 adds 9.9m to the water ray hit. A centimetre permits float
        // composition at world coordinates; it does not permit another placement height.
        return IsFinite(position) && float.IsFinite(surface) &&
            MathF.Abs(position.Z - (surface + PreviewHeight)) <= 0.01f;
    }

    public static CryBox ShipyardBox(CryBounds model, Vector3 position, float yaw, float buildRadius)
        => Box(model, position, yaw, new(buildRadius, ForwardClearance, 0));

    public static CryBox ClearanceBox(CryBounds model, Vector3 position, float yaw, float buildRadius)
    {
        // Native394d5d90 uses 0.3 of both the min/max sum and difference here.
        // The earlier living and shipyard overlap queries use the ordinary 0.5.
        var rotation = Matrix4x4.CreateRotationZ(yaw);
        var center = (model.Min + model.Max) * 0.3f + new Vector3(0, ForwardClearance, 0);
        var size = (model.Max - model.Min) * 0.3f + new Vector3(buildRadius, ForwardClearance, VerticalClearance);
        return new(Vector3.Transform(center, rotation) + position, size, rotation);
    }

    public static CryBox LivingBox(CryBounds model, Vector3 position, float yaw)
    {
        // Native394d1750 adds the local centre directly to the position for this query.
        return new(model.Center + new Vector3(0, ForwardClearance, 0) + position,
            model.HalfSize, Matrix4x4.CreateRotationZ(yaw));
    }

    private static CryBox Box(CryBounds model, Vector3 position, float yaw, Vector3 expansion)
    {
        var rotation = Matrix4x4.CreateRotationZ(yaw);
        return new(Vector3.Transform(model.Center + new Vector3(0, ForwardClearance, 0), rotation) + position,
            model.HalfSize + expansion, rotation);
    }

    public static bool Overlaps(CryBox candidate, CryBox neighbor) =>
        CryGeometryQueries.IntersectBox(new CryGeometryPart(neighbor, Matrix4x4.Identity,
            CryGeometryLayerRules.Solid, "", "shipyard"), Matrix4x4.Identity, candidate, Matrix4x4.Identity)
        != CryIntersection.Clear;

    public static ErrorMessageType CheckGeometry(CryBounds model, Vector3 position, float yaw, int buildRadius,
        float waterSurface, IEnumerable<CryBox> shipyards, Func<CryBox, int, CryIntersection> intersect)
    {
        if (!ValidBounds(model) || !float.IsFinite(yaw) || buildRadius < 0)
            return ErrorMessageType.CraftLocatingUnitIsNotExist;
        if (!IsOnWater(position, waterSurface))
            return ErrorMessageType.CraftLocatingUnitIsNotOnTheWaterOrDeepWater;
        var shipyardBox = ShipyardBox(model, position, yaw, buildRadius);
        if (shipyards.Any(neighbor => Overlaps(shipyardBox, neighbor)))
            return ErrorMessageType.CraftLocatingUnitIsTooCloseToOther;
        if (intersect(LivingBox(model, position, yaw), 8) != CryIntersection.Clear)
            return ErrorMessageType.CraftLocatingUnitIsTooCloseToUnit;
        return intersect(ClearanceBox(model, position, yaw, buildRadius), 0x10d) == CryIntersection.Clear
            ? ErrorMessageType.NoErrorMessage : ErrorMessageType.CraftLocatingUnitIsNotOnTheWaterOrDeepWater;
    }
}
