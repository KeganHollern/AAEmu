using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed record CryGeometryInstance(uint ObjectId, CryGeometryAsset Asset, Matrix4x4 Transform, int EntityType = 1, bool RayOnly = false)
{
    public bool IsVegetation { get; init; }
}
public readonly record struct CrySceneRayHit(CryRayHit Hit, uint ObjectId, bool IsTerrain, int PickingIndex = 0);

/// <summary>Queries authored instances. A missing shape must not become a clear result.</summary>
public sealed class CryGeometryScene(Func<CryBounds, IEnumerable<CryGeometryInstance>> queryInstances,
    Func<CryGeometryPart, bool> includePart)
{
    public CryIntersection Raycast(Vector3 origin, Vector3 direction, float maximumDistance,
        out CrySceneRayHit hit)
    {
        hit = default;
        var end = origin + direction * maximumDistance;
        var bounds = new CryBounds(Vector3.Min(origin, end), Vector3.Max(origin, end));
        var found = false;
        foreach (var instance in queryInstances(bounds))
        {
            if ((instance.EntityType & 7) == 0)
                continue;
            if (HasUnresolvedCollisionPose(instance.Asset))
                return CryIntersection.Indeterminate;
            foreach (var part in instance.RayOnly ? instance.Asset.Parts.Where(includePart) : SelectParts(instance, CryGeometryQueryUsage.Ray))
            {
                if (!CryGeometryQueries.GetBounds(part, instance.Transform).Intersects(bounds) ||
                    !CryGeometryQueries.Raycast(part, instance.Transform, origin, direction,
                        found ? hit.Hit.Distance : maximumDistance, true, out var candidate))
                    continue;
                hit = new CrySceneRayHit(candidate, instance.ObjectId, false, part.PickingIndex);
                found = true;
            }
        }
        return found ? CryIntersection.Intersects : CryIntersection.Clear;
    }

    private static bool HasUnresolvedCollisionPose(CryGeometryAsset asset) =>
        asset.PoseRequirements.Any(pose => pose.Playing && pose.Physicalized && pose.AffectsCollision);

    private IEnumerable<CryGeometryPart> SelectParts(CryGeometryInstance instance, CryGeometryQueryUsage usage)
    {
        var asset = instance.Asset;
        if (instance.IsVegetation)
        {
            foreach (var part in asset.Parts)
                if (includePart(part) && (CryGeometryLayerRules.GetVegetationUsage(part.PhysicsType) & usage) != 0)
                    yield return part;
            yield break;
        }
        foreach (var group in asset.Parts.GroupBy(part => part.PhysicsGroup))
        {
            var types = group.Select(part => part.PhysicsType).ToArray();
            foreach (var part in group)
                if (includePart(part) && (CryGeometryLayerRules.GetHousingUsage(part.PhysicsType, types, part.SpineCount) & usage) != 0)
                    yield return part;
        }
    }

    public CryIntersection IntersectBox(CryBox box)
    {
        var bounds = new CryBounds(-box.HalfSize, box.HalfSize).Transform(
            box.Orientation * Matrix4x4.CreateTranslation(box.Center));
        foreach (var instance in queryInstances(bounds))
        {
            if ((instance.EntityType & 0x1f) == 0 || instance.RayOnly)
                continue;
            if (HasUnresolvedCollisionPose(instance.Asset))
                return CryIntersection.Indeterminate;
            foreach (var part in SelectParts(instance, CryGeometryQueryUsage.PlacementOverlap))
            {
                if (!CryGeometryQueries.GetBounds(part, instance.Transform).Intersects(bounds))
                    continue;
                var result = CryGeometryQueries.IntersectBox(part, instance.Transform, box, Matrix4x4.Identity);
                if (result != CryIntersection.Clear)
                    return result;
            }
        }
        return CryIntersection.Clear;
    }
}
