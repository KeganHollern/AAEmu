#nullable enable

using System.Numerics;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics.Util;
using AAEmu.Game.Utils;
using Jitter2.Dynamics;
using Jitter2.LinearMath;
using NLog;

namespace AAEmu.Game.Physics;

/// <summary>
/// Converts static client waterfall brushes into controlled naval transition corridors. A hull
/// receives one downstream launch velocity, then gravity carries it to a verified lower water
/// surface. No continuous force is applied, so ship mass cannot change the trajectory.
/// </summary>
public sealed class ShipWaterfallInteraction
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal const float GravityMetersPerSecondSquared = 9.81f;
    internal const float LaunchUpwardMetersPerSecond = 1.25f;
    internal const float MinimumDropMeters = 3f;
    internal const float MaximumReplicableHorizontalSpeed = 15.5f;

    private const float ApproachPaddingMeters = 3f;
    private const float TopAlignmentToleranceMeters = 2.5f;
    private const float LandingSearchExtraMeters = 2f;
    private const float MinimumReceivingDepthMeters = 0.25f;
    private const float CorridorBeamMarginMeters = 0.5f;
    private const float LandingHorizontalDamping = 0.65f;
    private const float MaximumLandingRelativeSpeed = 8f;
    private const float MaximumLandingDownwardSpeed = 2.5f;
    private const float RecoveryCooldownSeconds = 2.5f;

    private readonly Dictionary<uint, TrackedShip> _trackedShips = new();

    private sealed class TrackedShip
    {
        public SafePose? LastSafePose;
        public SafePose? PendingApproach;
        public TransitState? Transit;
        public float RetryCooldown;
    }

    private struct SafePose
    {
        public JVector Position;
        public JQuaternion Orientation;
        public JVector Velocity;
        public JVector AngularVelocity;
        public float Speed;
        public float WaterlineTolerance;
        public float WaterSurface;
        public Vector3 WaterFlow;
        public float FloorLevel;
    }

    private sealed class TransitState
    {
        public required SafePose RecoveryPose;
        public required Vector2 Direction;
        public float SourceSurface;
        public float WaterfallBottom;
        public float ProjectedReceivingSurface;
        public float FarEdgeProjection;
        public float HullRadiusAlongDirection;
        public float WaterlineCenterOffset;
        public float ElapsedSeconds;
        public float MaximumSeconds;
    }

    /// <summary>
    /// Advances or starts a transition after the physics step and before ordinary ship contacts.
    /// Returns true while terrain, buoyancy, propulsion, towing, and hull contacts must remain off.
    /// </summary>
    public bool Update(Slave ship, TimeSpan deltaTime)
    {
        var body = ship.RigidBody;
        var world = ship.ParentWorld;
        if (body is null || world?.Water is null || ship.ShipController?.ShipModel is null || body.Shapes.Count == 0)
        {
            ship.WaterfallTransitActive = false;
            return false;
        }

        if (!_trackedShips.TryGetValue(ship.Id, out var tracked))
        {
            tracked = new TrackedShip();
            _trackedShips[ship.Id] = tracked;
        }

        var dt = Math.Clamp((float)deltaTime.TotalSeconds, 0f, 0.25f);
        tracked.RetryCooldown = MathF.Max(0f, tracked.RetryCooldown - dt);

        if (tracked.Transit is not null)
            return AdvanceTransit(ship, tracked, dt);

        // Defensive cleanup for a removed/reloaded state dictionary.
        ship.WaterfallTransitActive = false;

        // PhysicsManager refreshes the water from the last published Transform before syncing the body.
        // A moving ship's normal cache can therefore confirm the previous step here.
        if (tracked.PendingApproach is { } previous)
        {
            TryConfirmMeasuredApproach(ship, tracked, previous);
            tracked.PendingApproach = null;
        }

        var bounds = body.Shapes[0].WorldBoundingBox;
        var nearby = world.Water.GetWaterfallsIntersecting(
            bounds.Min.X - ApproachPaddingMeters,
            bounds.Min.Z - ApproachPaddingMeters,
            bounds.Min.Y - TopAlignmentToleranceMeters,
            bounds.Max.X + ApproachPaddingMeters,
            bounds.Max.Z + ApproachPaddingMeters,
            bounds.Max.Y + TopAlignmentToleranceMeters);

        if (nearby.Count == 0)
        {
            TryRememberSafePose(ship, tracked, bounds);
            return false;
        }

        if (tracked.RetryCooldown > 0f)
            return false;

        var position = new Vector3(body.Position.X, body.Position.Z, body.Position.Y);
        var sourceFlow = ship.CachedWaterFlow;
        var sourceSurface = ship.MeasuredWaterAndLandPosition == position
            ? ship.CachedWaterSurface
            : world.Water.GetWaterSurface(position, out sourceFlow);
        GetUnionBounds(nearby, out var waterfallMin, out var waterfallMax);
        if (!IsAtSourceLip(sourceSurface, bounds, waterfallMin, waterfallMax))
        {
            // Lower pools commonly overlap the waterfall brush's base. They remain ordinary
            // navigable water and must not behave like an invisible barrier after landing.
            TryRememberSafePose(ship, tracked, bounds);
            return false;
        }

        if (TryStartTransit(ship, tracked, bounds, waterfallMin, waterfallMax, sourceSurface, sourceFlow))
            return true;

        // An unsuitable corridor (no lower pool, too narrow, or a trajectory that would exceed
        // the movement packet range) is treated as a guarded lip, never as permission to enter the
        // bogus heightmap volume under the visual landscape.
        if (tracked.LastSafePose is { } safe && IsUpperWater(safe.WaterSurface, sourceSurface))
            Recover(ship, tracked, safe, "unsafe waterfall corridor");
        return false;
    }

    public void Remove(Slave ship)
    {
        _trackedShips.Remove(ship.Id);
        ship.WaterfallTransitActive = false;
    }

    private static void TryRememberSafePose(Slave ship, TrackedShip tracked, JBoundingBox bounds)
    {
        var body = ship.RigidBody!;
        if (ship.GroundContactLatched || !IsFinite(body))
            return;

        var pose = CapturePose(ship);
        pose.WaterlineTolerance = MathF.Max(3f, bounds.Max.Y - bounds.Min.Y);
        if (!TryConfirmMeasuredApproach(ship, tracked, pose))
            tracked.PendingApproach = pose;
    }

    private static bool TryConfirmMeasuredApproach(Slave ship, TrackedShip tracked, SafePose pose)
    {
        var p = pose.Position;
        if (ship.MeasuredWaterAndLandPosition != new Vector3(p.X, p.Z, p.Y) ||
            !(MathF.Abs(p.Y - ship.CachedWaterSurface) <= pose.WaterlineTolerance) ||
            !(ship.CachedFloorLevel <= ship.CachedWaterSurface - MinimumReceivingDepthMeters))
            return false;

        pose.FloorLevel = ship.CachedFloorLevel;
        pose.WaterSurface = ship.CachedWaterSurface;
        pose.WaterFlow = ship.CachedWaterFlow;
        tracked.LastSafePose = pose;
        return true;
    }

    private static bool TryStartTransit(Slave ship, TrackedShip tracked, JBoundingBox hullBounds,
        Vector3 waterfallMin, Vector3 waterfallMax, float sourceSurface, Vector3 sourceFlow)
    {
        var body = ship.RigidBody!;
        var direction = GetDownstreamDirection(sourceFlow, body);
        if (direction.LengthSquared() < 0.99f)
            return false;

        var halfX = (hullBounds.Max.X - hullBounds.Min.X) * 0.5f;
        var halfY = (hullBounds.Max.Z - hullBounds.Min.Z) * 0.5f;
        var hullRadiusAlong = MathF.Abs(direction.X) * halfX + MathF.Abs(direction.Y) * halfY;
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var hullFullBeam = 2f * (MathF.Abs(perpendicular.X) * halfX + MathF.Abs(perpendicular.Y) * halfY);
        var waterfallHalfX = (waterfallMax.X - waterfallMin.X) * 0.5f;
        var waterfallHalfY = (waterfallMax.Y - waterfallMin.Y) * 0.5f;
        var corridorWidth = 2f * (MathF.Abs(perpendicular.X) * waterfallHalfX +
                                  MathF.Abs(perpendicular.Y) * waterfallHalfY);
        if (corridorWidth < hullFullBeam + CorridorBeamMarginMeters)
            return false;

        var waterfallCenter = new Vector2(
            (waterfallMin.X + waterfallMax.X) * 0.5f,
            (waterfallMin.Y + waterfallMax.Y) * 0.5f);
        var waterfallRadiusAlong = MathF.Abs(direction.X) * waterfallHalfX +
                                   MathF.Abs(direction.Y) * waterfallHalfY;
        var farEdgeProjection = Vector2.Dot(waterfallCenter, direction) + waterfallRadiusAlong;
        var bodyCenter = new Vector2(body.Position.X, body.Position.Z);
        var clearanceToFarEdge = MathF.Max(0.5f,
            farEdgeProjection - Vector2.Dot(bodyCenter, direction) + hullRadiusAlong);

        if (!TryFindReceivingWater(ship, direction, sourceSurface, waterfallMin.Z,
                clearanceToFarEdge + LandingSearchExtraMeters,
                out var receivingSurface, out var launchDistance))
            return false;

        var drop = sourceSurface - receivingSurface;
        var fallTime = ComputeFallTime(drop, LaunchUpwardMetersPerSecond);
        var requiredSpeed = ComputeRequiredHorizontalSpeed(launchDistance, drop, LaunchUpwardMetersPerSecond);
        if (!float.IsFinite(requiredSpeed) || requiredSpeed > MaximumReplicableHorizontalSpeed)
            return false;

        requiredSpeed = MathF.Max(requiredSpeed, 4f);
        var recoveryPose = tracked.LastSafePose;
        if (recoveryPose is not { } safe || !IsUpperWater(safe.WaterSurface, sourceSurface))
            recoveryPose = FindFallbackPose(ship, sourceSurface, direction, hullRadiusAlong + 2f);
        if (recoveryPose is null)
            return false;
        tracked.Transit = new TransitState
        {
            RecoveryPose = recoveryPose.Value,
            Direction = direction,
            SourceSurface = sourceSurface,
            WaterfallBottom = waterfallMin.Z,
            ProjectedReceivingSurface = receivingSurface,
            FarEdgeProjection = farEdgeProjection,
            HullRadiusAlongDirection = hullRadiusAlong,
            WaterlineCenterOffset = body.Position.Y - sourceSurface,
            MaximumSeconds = Math.Clamp(fallTime + 3f, 4f, 9f)
        };

        ClearGroundState(ship);
        ship.WaterfallTransitActive = true;
        body.Velocity = ComposeLaunchVelocity(direction, requiredSpeed, body.Velocity.Y);
        body.AngularVelocity *= 0.35f;
        body.SetActivationState(true);
        Logger.Debug($"Waterfall launch ship={ship.ObjId} drop={drop:F1}m distance={launchDistance:F1}m speed={requiredSpeed:F1}m/s");
        return true;
    }

    private static bool TryFindReceivingWater(Slave ship, Vector2 direction, float sourceSurface,
        float waterfallBottom, float firstDistance, out float receivingSurface, out float launchDistance)
    {
        var world = ship.ParentWorld!;
        var body = ship.RigidBody!;
        ReadOnlySpan<float> extraDistances = [0f, 3f, 6f, 10f];
        foreach (var extra in extraDistances)
        {
            var distance = firstDistance + extra;
            var x = body.Position.X + direction.X * distance;
            var y = body.Position.Z + direction.Y * distance;
            var probe = new Vector3(x, y, waterfallBottom);
            if (!TryGetReceivingWater(world, probe, sourceSurface, waterfallBottom, out var surface, out _, out _))
                continue;

            receivingSurface = surface;
            launchDistance = distance;
            return true;
        }

        receivingSurface = 0f;
        launchDistance = 0f;
        return false;
    }

    private static bool TryGetReceivingWater(WorldInstance world, Vector3 point, float sourceSurface,
        float waterfallBottom, out float surface, out Vector3 flow, out float floor)
    {
        surface = world.Water.GetWaterSurface(point, out flow);
        floor = 0f;
        return float.IsFinite(surface) && sourceSurface - surface >= MinimumDropMeters &&
               surface >= waterfallBottom - 5f && world.TryGetHeight(point.X, point.Y, out floor) &&
               floor <= surface - MinimumReceivingDepthMeters;
    }

    private static bool AdvanceTransit(Slave ship, TrackedShip tracked, float dt)
    {
        var state = tracked.Transit!;
        var body = ship.RigidBody!;
        state.ElapsedSeconds += dt;

        if (!IsFinite(body) || state.ElapsedSeconds > state.MaximumSeconds ||
            body.Position.Y < state.ProjectedReceivingSurface - GetRecoveryDepth(ship))
        {
            Recover(ship, tracked, state.RecoveryPose, "waterfall flight failed");
            return false;
        }

        var projection = body.Position.X * state.Direction.X + body.Position.Z * state.Direction.Y;
        var reachedLowerPool = projection >= state.FarEdgeProjection - state.HullRadiusAlongDirection * 0.35f;
        // No eligible pool can be higher than the source minus the minimum drop.
        if (!ShouldCaptureLowerWater(body.Position.Y, body.Velocity.Y, state.SourceSurface - MinimumDropMeters,
                state.WaterlineCenterOffset, reachedLowerPool))
            return true;

        // Resolve the water at the hull. The flight can clear an overhang or pass the edge of the projected pool.
        var arrival = new Vector3(body.Position.X, body.Position.Z, body.Position.Y - state.WaterlineCenterOffset);
        if (!TryGetReceivingWater(ship.ParentWorld!, arrival, state.SourceSurface, state.WaterfallBottom,
                out var surface, out var flow, out var floor) ||
            !AtCaptureHeight(body.Position.Y, surface, state.WaterlineCenterOffset))
            return true;
        CaptureLowerWater(ship, tracked, state, surface, flow, floor);
        return false;
    }

    private static void CaptureLowerWater(Slave ship, TrackedShip tracked, TransitState state,
        float surface, Vector3 flow, float floor)
    {
        var body = ship.RigidBody!;
        var targetCenterY = surface + state.WaterlineCenterOffset;
        if (body.Position.Y < targetCenterY - 0.35f)
            body.Position = body.Position with { Y = targetCenterY - 0.35f };

        var waterVelocity = new JVector(flow.X, flow.Z, flow.Y);
        var relative = body.Velocity - waterVelocity;
        var relativeHorizontal = new JVector(relative.X, 0f, relative.Z) * LandingHorizontalDamping;
        var relativeSpeed = MathF.Sqrt(relativeHorizontal.X * relativeHorizontal.X +
                                       relativeHorizontal.Z * relativeHorizontal.Z);
        if (relativeSpeed > MaximumLandingRelativeSpeed)
            relativeHorizontal *= MaximumLandingRelativeSpeed / relativeSpeed;

        body.Velocity = new JVector(
            waterVelocity.X + relativeHorizontal.X,
            MathF.Max(body.Velocity.Y, -MaximumLandingDownwardSpeed),
            waterVelocity.Z + relativeHorizontal.Z);
        body.AngularVelocity *= 0.5f;
        body.SetActivationState(true);

        ship.MeasuredWaterAndLandPosition = null;
        ship.CachedWaterSurface = surface;
        ship.CachedWaterFlow = flow;
        ship.CachedFloorLevel = floor;
        ClearGroundState(ship);
        ship.WaterfallTransitActive = false;
        tracked.Transit = null;
        tracked.RetryCooldown = RecoveryCooldownSeconds;
        ShipShipInteraction.SyncSlaveSpeedFromBowVelocity(ship);
        tracked.LastSafePose = CapturePose(ship);
        Logger.Debug($"Waterfall capture ship={ship.ObjId} surface={surface:F1}");
    }

    private static void Recover(Slave ship, TrackedShip tracked, SafePose pose, string reason)
    {
        var body = ship.RigidBody!;
        body.Position = pose.Position;
        body.Orientation = pose.Orientation;
        body.Velocity = pose.Velocity;
        body.AngularVelocity = pose.AngularVelocity;
        body.SetActivationState(true);
        ship.Speed = pose.Speed;
        ship.MeasuredWaterAndLandPosition = null;
        ship.CachedWaterSurface = pose.WaterSurface;
        ship.CachedWaterFlow = pose.WaterFlow;
        ship.CachedFloorLevel = pose.FloorLevel;
        ClearGroundState(ship);
        ship.WaterfallTransitActive = false;
        tracked.Transit = null;
        tracked.RetryCooldown = RecoveryCooldownSeconds;
        Logger.Warn($"Recovered ship={ship.ObjId} to safe water pose: {reason}");
    }

    private static SafePose CapturePose(Slave ship) => new()
    {
        Position = ship.RigidBody!.Position,
        Orientation = ship.RigidBody.Orientation,
        Velocity = ship.RigidBody.Velocity,
        AngularVelocity = ship.RigidBody.AngularVelocity,
        Speed = ship.Speed,
        WaterSurface = ship.CachedWaterSurface,
        WaterFlow = ship.CachedWaterFlow,
        FloorLevel = ship.CachedFloorLevel
    };

    private static bool IsUpperWater(float surface, float sourceSurface) =>
        MathF.Abs(surface - sourceSurface) <= TopAlignmentToleranceMeters;

    private static SafePose? FindFallbackPose(Slave ship, float sourceSurface, Vector2 downstream, float upstreamDistance)
    {
        var source = CapturePose(ship);
        // Prefer the historical upstream offset. A ship first observed at the lip can
        // also use its measured current position. At a false terrain ridge on the lip,
        // check the same bounded offsets used for the receiving pool, this time upstream.
        ReadOnlySpan<float> distances = [upstreamDistance, 0f, upstreamDistance + 3f,
            upstreamDistance + 6f, upstreamDistance + 10f];
        foreach (var distance in distances)
        {
            var x = source.Position.X - downstream.X * distance;
            var y = source.Position.Z - downstream.Y * distance;
            var p = new Vector3(x, y, sourceSurface);
            var surface = ship.ParentWorld.Water.GetWaterSurface(p, out var flow);
            if (!IsUpperWater(surface, sourceSurface) || !ship.ParentWorld.TryGetHeight(x, y, out var floor) ||
                !(floor <= surface - MinimumReceivingDepthMeters))
                continue;

            var pose = source;
            pose.Position = new JVector(x, source.Position.Y + (surface - sourceSurface), y);
            pose.Velocity = JVector.Zero;
            pose.AngularVelocity = JVector.Zero;
            pose.Speed = 0f;
            pose.FloorLevel = floor;
            pose.WaterSurface = surface;
            pose.WaterFlow = flow;
            return pose;
        }
        return null;
    }

    private static Vector2 GetDownstreamDirection(Vector3 waterFlow, RigidBody body)
    {
        var direction = new Vector2(waterFlow.X, waterFlow.Y);
        if (direction.LengthSquared() < 0.04f)
            direction = new Vector2(body.Velocity.X, body.Velocity.Z);
        if (direction.LengthSquared() < 0.04f)
        {
            var rpy = PhysicsUtil.GetYawPitchRollFromMatrix(JMatrix.CreateFromQuaternion(body.Orientation));
            var bow = rpy.Item1 + MathUtil.HalfPi;
            direction = new Vector2(MathF.Cos(bow), MathF.Sin(bow));
        }

        return direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.Zero;
    }

    private static void GetUnionBounds(IReadOnlyList<WaterfallArea> areas, out Vector3 min, out Vector3 max)
    {
        min = areas[0].Min;
        max = areas[0].Max;
        for (var i = 1; i < areas.Count; i++)
        {
            min = Vector3.Min(min, areas[i].Min);
            max = Vector3.Max(max, areas[i].Max);
        }
    }

    private static bool IsAtSourceLip(float sourceSurface, JBoundingBox hullBounds,
        Vector3 waterfallMin, Vector3 waterfallMax) =>
        MathF.Abs(sourceSurface - waterfallMax.Z) <= TopAlignmentToleranceMeters &&
        sourceSurface - waterfallMin.Z >= MinimumDropMeters &&
        hullBounds.Min.Y <= waterfallMax.Z + TopAlignmentToleranceMeters &&
        hullBounds.Max.Y >= waterfallMax.Z - TopAlignmentToleranceMeters;

    private static float GetRecoveryDepth(Slave ship)
    {
        var hullHeight = ship.RigidBody!.Shapes.Count > 0
            ? ship.RigidBody.Shapes[0].WorldBoundingBox.Max.Y - ship.RigidBody.Shapes[0].WorldBoundingBox.Min.Y
            : ship.ShipController!.ShipModel.MassBoxSizeZ * MathF.Max(ship.Scale, 0.01f);
        return MathF.Max(8f, hullHeight * 2f);
    }

    private static void ClearGroundState(Slave ship)
    {
        ship.GroundContactLatched = false;
        ship.GroundedByStern = false;
        ship.GroundContactLatchedTime = 0f;
        ship.GroundContactFloorSmoothingSeeded = false;
        ship.GroundPitchFloorSmoothingSeeded = false;
        ship.ShoreGroundDamageSecondsAccumulator = 0f;
    }

    private static bool IsFinite(RigidBody body) =>
        float.IsFinite(body.Position.X) && float.IsFinite(body.Position.Y) && float.IsFinite(body.Position.Z) &&
        float.IsFinite(body.Velocity.X) && float.IsFinite(body.Velocity.Y) && float.IsFinite(body.Velocity.Z) &&
        float.IsFinite(body.Orientation.X) && float.IsFinite(body.Orientation.Y) &&
        float.IsFinite(body.Orientation.Z) && float.IsFinite(body.Orientation.W);

    internal static float ComputeFallTime(float dropMeters, float upwardMetersPerSecond)
    {
        if (!float.IsFinite(dropMeters) || dropMeters <= 0f || !float.IsFinite(upwardMetersPerSecond))
            return 0f;
        return (upwardMetersPerSecond + MathF.Sqrt(upwardMetersPerSecond * upwardMetersPerSecond +
                                                   2f * GravityMetersPerSecondSquared * dropMeters)) /
               GravityMetersPerSecondSquared;
    }

    internal static float ComputeRequiredHorizontalSpeed(float distanceMeters, float dropMeters,
        float upwardMetersPerSecond)
    {
        var fallTime = ComputeFallTime(dropMeters, upwardMetersPerSecond);
        if (fallTime <= 1e-4f || !float.IsFinite(distanceMeters) || distanceMeters < 0f)
            return float.PositiveInfinity;
        const float clearanceSafetyMultiplier = 1.12f;
        return distanceMeters / fallTime * clearanceSafetyMultiplier;
    }

    internal static JVector ComposeLaunchVelocity(Vector2 downstream, float horizontalSpeed,
        float existingVerticalVelocity)
    {
        if (downstream.LengthSquared() < 1e-6f || !float.IsFinite(horizontalSpeed))
            return new JVector(0f, MathF.Max(existingVerticalVelocity, LaunchUpwardMetersPerSecond), 0f);
        downstream = Vector2.Normalize(downstream);
        horizontalSpeed = Math.Clamp(horizontalSpeed, 0f, MaximumReplicableHorizontalSpeed);
        return new JVector(
            downstream.X * horizontalSpeed,
            MathF.Max(existingVerticalVelocity, LaunchUpwardMetersPerSecond),
            downstream.Y * horizontalSpeed);
    }

    internal static bool ShouldCaptureLowerWater(float bodyHeight, float verticalVelocity,
        float receivingSurface, float waterlineCenterOffset, bool reachedLowerPool) =>
        reachedLowerPool && verticalVelocity <= 0f && AtCaptureHeight(bodyHeight, receivingSurface, waterlineCenterOffset);

    private static bool AtCaptureHeight(float bodyHeight, float surface, float waterlineCenterOffset) =>
        bodyHeight <= surface + MathF.Max(0.35f, waterlineCenterOffset + 0.35f);
}
