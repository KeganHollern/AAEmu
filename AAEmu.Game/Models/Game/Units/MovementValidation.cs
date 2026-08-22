using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Models.Game.Units;

/// <summary>
/// Server-side sanity checks applied at the packet boundary to client-authored
/// movement requests (CSMoveUnitPacket). Complements the passive periodic
/// analysis performed by <see cref="SusManager"/> by rejecting impossible
/// displacements before they mutate authoritative world state.
///
/// Enforcement is strike-based: isolated spikes (knockback, lag bursts, physics
/// pushes that the server applied outside this packet path) are accepted and
/// logged, while repeated violations within a short window are rejected and the
/// authoring client is snapped back to the authoritative position.
/// </summary>
public static class MovementValidation
{
    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Maximum accepted horizontal speed in m/s. Deliberately generous compared
    /// to retail movement (~6.5 m/s sprint, ~12 m/s mounted) to avoid false
    /// positives from lag bursts, gliders, and physics pushes.
    /// </summary>
    public const float MaxHorizontalSpeed = 30f;

    /// <summary>
    /// Absolute horizontal displacement guard per request regardless of timing.
    /// </summary>
    public const float MaxSingleMoveDistance = 100f;

    /// <summary>
    /// Consecutive violations within <see cref="StrikeWindow"/> before requests
    /// start being rejected.
    /// </summary>
    public const int StrikeLimit = 3;

    /// <summary>
    /// Violations older than this window do not accumulate toward rejection.
    /// </summary>
    public static readonly TimeSpan StrikeWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Minimum interval between resamples; requests arriving sooner are accepted
    /// without updating the baseline so short gaps cannot produce spikes.
    /// </summary>
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Minimum interval between audit entries for the same mover.
    /// </summary>
    private static readonly TimeSpan AuditThrottle = TimeSpan.FromSeconds(15);

    private sealed record AcceptedMove(Vector3 Position, DateTime Time);

    private sealed record MoverState(AcceptedMove Last, DateTime LastAudit, int Strikes, DateTime LastStrike);

    private static readonly MoverState InitialState = new(new AcceptedMove(Vector3.Zero, DateTime.MinValue), DateTime.MinValue, 0, DateTime.MinValue);

    private static readonly ConcurrentDictionary<uint, MoverState> Movers = new();

    /// <summary>
    /// Validates a claimed world position for a client-controlled unit.
    /// Returns true when the request may be applied. Rejected requests must not
    /// mutate world state; callers should correct the authoring client.
    /// Units attached to a parent (vehicles, mounts, moving platforms) and
    /// skill-controller moves are carried or server-driven, so their baseline is
    /// refreshed instead of validated.
    /// </summary>
    /// <param name="unit">The unit whose position is being authored.</param>
    /// <param name="controller">The character authoring the request, used for audit attribution.</param>
    /// <param name="claimedWorldPosition">The requested world-space position.</param>
    /// <param name="context">Short description of the movement kind for logs.</param>
    /// <param name="isSkillController">True when the move is flagged as a skill-controller move (HasScTypeAndPhase).</param>
    /// <returns>True when the request may be applied.</returns>
    public static bool TryAccept(BaseUnit unit, Character controller, Vector3 claimedWorldPosition, string context,
        bool isSkillController = false)
    {
        var now = DateTime.UtcNow;
        var state = Movers.GetOrAdd(unit.ObjId,
            _ => InitialState with { Last = new AcceptedMove(claimedWorldPosition, now) });

        // Carried units are moved by their parent, and skill-controller moves are
        // server-driven jumps; refresh the baseline so the next self-propelled
        // request is judged from the new location instead of spiking.
        if (isSkillController || unit.Transform?.Parent != null || controller?.DisabledSetPosition == true)
        {
            Movers[unit.ObjId] = state with { Last = new AcceptedMove(claimedWorldPosition, now), Strikes = 0 };
            return true;
        }

        var delta = claimedWorldPosition - state.Last.Position;
        var horizontalDistance = (delta with { Z = 0 }).Length();
        var elapsed = (now - state.Last.Time).TotalSeconds;

        if (horizontalDistance <= MaxSingleMoveDistance &&
            (elapsed < MinSampleInterval.TotalSeconds || elapsed <= 0 ||
             horizontalDistance / elapsed <= MaxHorizontalSpeed))
        {
            Movers[unit.ObjId] = state with { Last = new AcceptedMove(claimedWorldPosition, now), Strikes = 0 };
            return true;
        }

        // Violations outside the strike window start a fresh streak.
        var strikes = (now - state.LastStrike) > StrikeWindow ? 1 : state.Strikes + 1;

        var reason = horizontalDistance > MaxSingleMoveDistance
            ? $"displacement {horizontalDistance:F1} m exceeds the {MaxSingleMoveDistance:F0} m single-request limit"
            : $"speed {horizontalDistance / Math.Max(elapsed, 0.001):F1} m/s exceeds the {MaxHorizontalSpeed:F0} m/s limit";

        if (strikes < StrikeLimit)
        {
            // Tolerated spike: accept and resynchronize so a one-off server-side
            // displacement (knockback, physics push) cannot cascade into rejection.
            Logger.Warn(
                "Tolerated {Context} movement anomaly for {Unit} ({ObjId}) authored by {Character} (strike {Strikes}/{Limit}): {Reason}",
                context, unit.Name, unit.ObjId, controller?.Name ?? "<unknown>", strikes, StrikeLimit, reason);
            Movers[unit.ObjId] = state with
            {
                Last = new AcceptedMove(claimedWorldPosition, now),
                Strikes = strikes,
                LastStrike = now
            };
            return true;
        }

        // Repeated violations: reject without moving the baseline so the caller
        // can snap the authoring client back to the last accepted position.
        if ((now - state.LastAudit) >= AuditThrottle)
        {
            Movers[unit.ObjId] = state with { LastAudit = now, Strikes = strikes, LastStrike = now };
            Logger.Warn(
                "Rejected {Context} movement for {Unit} ({ObjId}) authored by {Character} (strike {Strikes}/{Limit}): {Reason}",
                context, unit.Name, unit.ObjId, controller?.Name ?? "<unknown>", strikes, StrikeLimit, reason);
            if (controller != null)
            {
                SusManager.Instance.LogActivity(SusManager.CategoryCheating, controller,
                    $"{context} movement rejected for {unit.Name}: {reason}");
            }
        }
        else
        {
            Movers[unit.ObjId] = state with { Strikes = strikes, LastStrike = now };
        }

        return false;
    }

    /// <summary>
    /// Clears tracked state for a unit, e.g. after teleports or despawn.
    /// </summary>
    public static void Reset(uint objId)
    {
        _ = Movers.TryRemove(objId, out _);
    }
}
