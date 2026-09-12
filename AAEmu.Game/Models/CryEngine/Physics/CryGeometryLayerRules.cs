namespace AAEmu.Game.Models.CryEngine.Physics;

[Flags]
public enum CryGeometryQueryUsage
{
    None = 0,
    Ray = 1,
    PlacementOverlap = 2
}

/// <summary>
/// r208022 CStatObj::Physicalize selection for default rays and housing's
/// PrimitiveWorldIntersection geomFlagsAny=3. Apply separately to each statobject.
/// </summary>
public static class CryGeometryLayerRules
{
    // CVegetation::Physicalize retains ordinary solid flags for slot0. Its extra
    // foliage proxies have neither geom_colltype0/1 nor geom_colltype_ray.
    public static CryGeometryQueryUsage GetVegetationUsage(int physicsType) => physicsType == 0x1000
        ? CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap : CryGeometryQueryUsage.None;

    public const int Solid = 0x1000;
    public const int Ray = 0x1001;
    public const int Obstruct = 0x1002;

    public static CryGeometryQueryUsage GetHousingUsage(int physicsType, IReadOnlyList<int> groupTypes, int spineCount)
    {
        if (!groupTypes.Contains(physicsType))
            return CryGeometryQueryUsage.None;
        var otherCount = groupTypes.Count(type => type != Solid);
        var solidCount = groupTypes.Count - otherCount;
        if (spineCount > 0 && solidCount <= 1 &&
            (otherCount == 1 || otherCount == 2 && groupTypes.Contains(Ray) && groupTypes.Contains(Obstruct)))
        {
            // Native foliage flags302000/104000/202000 contain neither ray8000 nor overlap3.
            return physicsType == Solid
                ? CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap
                : CryGeometryQueryUsage.None;
        }
        if (otherCount == 1 && groupTypes.Count == 2)
        {
            // One ray geometry with a distinct solid collision proxy, including an obstruct proxy.
            return physicsType == Solid ? CryGeometryQueryUsage.PlacementOverlap : CryGeometryQueryUsage.Ray;
        }
        return physicsType switch
        {
            Solid => CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap,
            Ray => CryGeometryQueryUsage.Ray,
            _ => CryGeometryQueryUsage.None
        };
    }
}
