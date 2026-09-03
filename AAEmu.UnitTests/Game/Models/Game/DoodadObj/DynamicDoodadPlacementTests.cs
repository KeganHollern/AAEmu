using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

public class DynamicDoodadPlacementTests
{
    [Test]
    public async Task CreateForwardWorldPosition_ParentedRotatedSource_UsesDetachedWorldTransform()
    {
        using var parent = new Transform(
            null,
            null,
            new Vector3(100f, 200f, 30f),
            new Vector3(0f, 0f, MathF.PI / 2f));
        using var source = new Transform(
            null,
            parent,
            new Vector3(2f, 3f, 4f),
            new Vector3(0f, 0f, MathF.PI / 4f));

        var position = DynamicDoodadPlacement.CreateForwardWorldPosition(source, 2f);

        var diagonalOffset = MathF.Sqrt(2f);
        await Assert.That(position.X).IsEqualTo(97f - diagonalOffset).Within(0.001f);
        await Assert.That(position.Y).IsEqualTo(202f - diagonalOffset).Within(0.001f);
        await Assert.That(position.Z).IsEqualTo(34f).Within(0.001f);
        await Assert.That(position.Roll).IsEqualTo(0f).Within(0.001f);
        await Assert.That(position.Pitch).IsEqualTo(0f).Within(0.001f);
        await Assert.That(position.Yaw).IsEqualTo(3f * MathF.PI / 4f).Within(0.001f);
        await Assert.That(source.Local.Position).IsEqualTo(new Vector3(2f, 3f, 4f));
    }

    [Test]
    [Arguments(0f)]
    [Arguments(-12.5f)]
    [Arguments(42f)]
    public async Task TryResolve_TerrainSurface_UsesValidHeight(float surfaceHeight)
    {
        var candidate = new Vector3(10f, 20f, surfaceHeight + 1f);

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = TerrainSurface(surfaceHeight);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(new Vector3(candidate.X, candidate.Y, surfaceHeight));
    }

    [Test]
    [Arguments(90f, 92f)]
    [Arguments(-1f, -2f)]
    public async Task TryResolve_NavigationWaypointOnNearbyLayer_UsesWaypointHeight(float candidateHeight,
        float surfaceHeight)
    {
        var candidate = new Vector3(10f, 20f, candidateHeight);
        var reference = new BaiSurfaceReference(
            BaiSurfaceReferenceKind.NavigationNode,
            1,
            2,
            BaiNavigationType.WaypointHuman,
            new Vector3(10f, 20f, surfaceHeight));

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = new GroundSurfaceResult(surfaceHeight, GroundSurfaceSource.NavigationNode,
                    GroundSurfaceDecision.NavigationHeightPreserved, GroundSurfaceFailure.None, reference);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(new Vector3(10f, 20f, surfaceHeight));
    }

    [Test]
    public async Task TryResolve_UnavailableSurface_PreservesCandidate()
    {
        var candidate = new Vector3(10f, 20f, -4f);

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = default;
                return false;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(candidate);
    }

    [Test]
    public async Task TryResolve_MissingGeoData_PreservesFiniteCandidate()
    {
        var candidate = new Vector3(10f, 20f, -4f);

        var resolved = DynamicDoodadPlacement.TryResolve(
            null,
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(candidate);
    }

    [Test]
    public async Task TryResolve_UnresolvedTypedResult_PreservesCandidate()
    {
        var candidate = new Vector3(10f, 20f, 8f);

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = new GroundSurfaceResult(0f, GroundSurfaceSource.None, GroundSurfaceDecision.None,
                    GroundSurfaceFailure.InvalidSample, null);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(candidate);
    }

    [Test]
    public async Task TryResolve_TerrainUnavailableFallback_PreservesCandidate()
    {
        var candidate = new Vector3(10f, 20f, 8f);

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = new GroundSurfaceResult(10f, GroundSurfaceSource.NavigationNode,
                    GroundSurfaceDecision.TerrainUnavailableFallback, GroundSurfaceFailure.None, null);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(candidate);
    }

    [Test]
    [Arguments(5f, true)]
    [Arguments(5.01f, false)]
    public async Task TryResolve_TerrainHeightDelta_OnlyUsesNearbyLayer(float heightDelta, bool appliesSurface)
    {
        var candidate = new Vector3(10f, 20f, 20f);
        var surfaceHeight = candidate.Z - heightDelta;

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = TerrainSurface(surfaceHeight);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement.Z).IsEqualTo(appliesSurface ? surfaceHeight : candidate.Z);
    }

    [Test]
    public async Task TryResolve_ParentedPolicy_PreservesHeightWithoutSamplingSurface()
    {
        var candidate = new Vector3(10f, 20f, 80f);
        var sampleCount = 0;

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.PreserveParentedHeight,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                sampleCount++;
                surface = TerrainSurface(0f);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(sampleCount).IsEqualTo(0);
        await Assert.That(placement).IsEqualTo(candidate);
    }

    [Test]
    public async Task TryResolve_InvalidCandidate_RejectsWithoutSamplingSurface()
    {
        var candidate = new Vector3(float.NaN, 20f, 80f);
        var sampleCount = 0;

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                sampleCount++;
                surface = TerrainSurface(0f);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsFalse();
        await Assert.That(sampleCount).IsEqualTo(0);
        await Assert.That(float.IsNaN(placement.X)).IsTrue();
    }

    [Test]
    public async Task TryResolve_NonFiniteSurfaceHeight_PreservesCandidate()
    {
        var candidate = new Vector3(10f, 20f, 8f);

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 _, out GroundSurfaceResult surface) =>
            {
                surface = TerrainSurface(float.PositiveInfinity);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(placement).IsEqualTo(candidate);
    }

    [Test]
    public async Task TryResolve_GroundPolicy_PassesExactCandidateToResolver()
    {
        var candidate = new Vector3(10.25f, -20.5f, 30.75f);
        Vector3? sampledPosition = null;

        var resolved = DynamicDoodadPlacement.TryResolve(
            candidate,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 position, out GroundSurfaceResult surface) =>
            {
                sampledPosition = position;
                surface = TerrainSurface(candidate.Z);
                return true;
            },
            out var placement);

        await Assert.That(resolved).IsTrue();
        await Assert.That(sampledPosition).IsNotNull();
        await Assert.That(sampledPosition!.Value).IsEqualTo(candidate);
        await Assert.That(placement).IsEqualTo(candidate);
    }

    private static GroundSurfaceResult TerrainSurface(float height)
    {
        return new GroundSurfaceResult(height, GroundSurfaceSource.Terrain,
            GroundSurfaceDecision.TerrainOnly, GroundSurfaceFailure.None, null);
    }
}
