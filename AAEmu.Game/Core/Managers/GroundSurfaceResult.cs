using System.Numerics;

using AAEmu.Game.Models.CryEngine.Entities;

namespace AAEmu.Game.Core.Managers;

public enum GroundSurfaceSource
{
    None,
    Terrain,
    NavigationNode,
    ObstacleVertex
}

public enum GroundSurfaceDecision
{
    None,
    TerrainOnly,
    OutdoorTriangulation,
    ObstacleRejected,
    NavigationHeightPreserved,
    TerrainUnavailableFallback
}

public enum GroundSurfaceFailure
{
    None,
    InvalidPosition,
    Unavailable,
    InvalidSample,
    ResolverError
}

public enum BaiSurfaceReferenceKind
{
    NavigationNode,
    ObstacleVertex
}

/// <summary>
/// Identifies the BAI sample considered by a ground-surface query. This is a diagnostic reference,
/// not a stable surface-layer identifier.
/// </summary>
public readonly record struct BaiSurfaceReference(
    BaiSurfaceReferenceKind Kind,
    uint ZoneId,
    long? NodeId,
    BaiNavigationType NavigationType,
    Vector3 Position);

/// <summary>
/// Describes both the selected ground surface and why it was selected.
/// </summary>
public readonly record struct GroundSurfaceResult(
    float Height,
    GroundSurfaceSource Source,
    GroundSurfaceDecision Decision,
    GroundSurfaceFailure Failure,
    BaiSurfaceReference? BaiReference)
{
    public bool IsResolved => Source != GroundSurfaceSource.None && Failure == GroundSurfaceFailure.None;
}
