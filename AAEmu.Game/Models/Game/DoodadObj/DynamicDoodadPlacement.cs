using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.DoodadObj;

internal enum DynamicDoodadPlacementPolicy
{
    // Ground a standalone server-generated doodad only when the typed result remains on its candidate layer.
    GroundToNearbySurface,
    // A parented source already supplies the composed world height; static geodata cannot describe its platform.
    PreserveParentedHeight
}

internal static class DynamicDoodadPlacement
{
    // Match the existing dynamic mate/effect endpoint guard: allow ordinary slope and pivot corrections,
    // but keep the source height when the resolver selects a clearly different vertical layer.
    internal const float MaximumSurfaceHeightDelta = 5f;

    internal delegate bool GroundSurfaceResolver(Vector3 position, out GroundSurfaceResult surface);

    internal static WorldSpawnPosition CreateForwardWorldPosition(Transform source, float distance)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var detached = source.CloneDetached();
        detached.Local.AddDistanceToFront(distance);
        return detached.CloneAsSpawnPosition();
    }

    internal static bool TryResolve(AiGeoDataManager geoData, Vector3 candidate,
        DynamicDoodadPlacementPolicy policy, out Vector3 placement)
    {
        if (!TryBeginResolution(candidate, policy, out placement, out var requiresSurface))
            return false;

        if (!requiresSurface || geoData is null)
            return true;

        var resolved = geoData.TryGetGroundSurface(candidate, out var surface);
        ApplySurface(candidate, resolved, surface, ref placement);
        return true;
    }

    internal static bool TryResolve(Vector3 candidate, DynamicDoodadPlacementPolicy policy,
        GroundSurfaceResolver surfaceResolver, out Vector3 placement)
    {
        if (!TryBeginResolution(candidate, policy, out placement, out var requiresSurface))
            return false;

        if (!requiresSurface || surfaceResolver is null)
            return true;

        var resolved = surfaceResolver(candidate, out var surface);
        ApplySurface(candidate, resolved, surface, ref placement);
        return true;
    }

    private static void ApplySurface(Vector3 candidate, bool resolved, GroundSurfaceResult surface,
        ref Vector3 placement)
    {
        if (!resolved || !surface.IsResolved ||
            !IsTrustedSurface(surface) || !float.IsFinite(surface.Height) ||
            MathF.Abs(candidate.Z - surface.Height) > MaximumSurfaceHeightDelta)
            return;

        placement.Z = surface.Height;
    }

    private static bool TryBeginResolution(Vector3 candidate, DynamicDoodadPlacementPolicy policy,
        out Vector3 placement, out bool requiresSurface)
    {
        placement = candidate;
        requiresSurface = false;
        if (!IsFinite(candidate))
            return false;

        if (policy == DynamicDoodadPlacementPolicy.PreserveParentedHeight)
            return true;

        if (policy != DynamicDoodadPlacementPolicy.GroundToNearbySurface)
            return false;

        requiresSurface = true;
        return true;
    }

    private static bool IsTrustedSurface(GroundSurfaceResult surface)
    {
        // This compatibility result is a sparse BAI point used only because rendered terrain was unavailable.
        if (surface.Decision == GroundSurfaceDecision.TerrainUnavailableFallback)
            return false;

        return surface.Source == GroundSurfaceSource.Terrain ||
               surface is
               {
                   Source: GroundSurfaceSource.NavigationNode,
                   Decision: GroundSurfaceDecision.NavigationHeightPreserved
               };
    }

    private static bool IsFinite(Vector3 position)
    {
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }
}
