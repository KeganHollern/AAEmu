using System.Globalization;
using System.Numerics;
using AAEmu.Game.Models.Game.AI.Utils;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.AI.v2.Controls;

public class AiPathHandler(NpcAi aiOwner)
{
    public NpcAi Owner { get; } = aiOwner;

    /// <summary>
    /// Loaded AI Path Points
    /// </summary>
    public List<AiPathPoint> AiPathPoints { get; set; } = [];

    /// <summary>
    /// Queue of locations to go to next
    /// </summary>
    public Queue<AiPathPoint> AiPathPointsRemaining { get; set; } = new();

    public bool AiPathLooping { get; set; } = true; // Needs to be set to true to trigger initial loading into queue
    /// <summary>
    /// Speed multiplier when moving on the Path
    /// </summary>
    public float AiPathSpeed { get; set; } = 1f;

    /// <summary>
    /// Stance to use when moving on the Path; 5-walk, 4-run, 3-stand still.
    /// Null means "derive it from the actual movement speed" (aaemu-cluster#92); a path file's
    /// ActorFlags action sets it explicitly.
    /// </summary>
    public byte? AiPathActorFlags { get; set; }

    /// <summary>
    /// Currently targeted position for path movement
    /// </summary>
    public Vector3 TargetPosition { get; set; } = Vector3.Zero;

    /// <summary>
    /// Smallest horizontal radius that counts as "reached this waypoint". Small NPCs have a Scale below
    /// the distance a single step covers, which used to make waypoint advance unreachable.
    /// </summary>
    private const float MinWaypointArrivalRadius = 0.5f;

    /// <summary>
    /// Does path movement and dequeuing as needed
    /// </summary>
    /// <param name="delta"></param>
    /// <returns>Returns true as long as there is still unhandled path movement</returns>
    public bool RunCurrentPath(TimeSpan delta)
    {
        // Queue empty? refill!
        if (AiPathPointsRemaining.Count <= 0 && AiPathPoints.Count > 0 && AiPathLooping)
        {
            AiPathLooping = false;
            foreach (var aiPathPoint in AiPathPoints)
            {
                AiPathPointsRemaining.Enqueue(aiPathPoint);
            }
        }

        // Are we there yet? aaemu-cluster#92: horizontal test with a vertical guard, the 3D test used to
        // be defeated forever by a sub-meter floor disagreement (and by Scale < one movement step).
        var arrivalRadius = Math.Max(Owner.Owner.Template.Scale, MinWaypointArrivalRadius);
        if (TargetPosition != Vector3.Zero &&
            AiUtils.HasReachedPathWaypoint(Owner.Owner.Transform.World.Position, TargetPosition, arrivalRadius))
        {
            TargetPosition = Vector3.Zero;
        }

        // No current target? Set it!
        if (TargetPosition == Vector3.Zero && AiPathPointsRemaining.Count > 0)
        {
            var nextPos = AiPathPointsRemaining.Dequeue();
            switch (nextPos.Action)
            {
                case AiPathPointAction.None:
                    break;
                case AiPathPointAction.DisableLoop:
                    AiPathLooping = false;
                    break;
                case AiPathPointAction.EnableLoop:
                    AiPathLooping = true;
                    break;
                case AiPathPointAction.Speed:
                    if (float.TryParse(nextPos.Param, CultureInfo.InvariantCulture, out var newSpeed))
                        AiPathSpeed = newSpeed;
                    break;
                case AiPathPointAction.ActorFlags:
                    if (byte.TryParse(nextPos.Param, out var newStance))
                        AiPathActorFlags = newStance;
                    break;
                case AiPathPointAction.ReturnToCommandSet:
                    Owner.GoToRunCommandSet();
                    return true;
                default:
                    throw new NotSupportedException($"Not supported nextPos.Action value: {nextPos.Action}");
            }

            // Set next move point if it's not zero
            if (!nextPos.Position.Equals(Vector3.Zero))
            {
                TargetPosition = nextPos.Position;
            }
        }

        // We know where to go? Then go that direction
        if (TargetPosition != Vector3.Zero)
        {
            var moveSpeed = Owner.GetRealMovementSpeed(AiPathSpeed);
            // aaemu-cluster#92: unless the path file names its own ActorFlags, the gait has to follow the
            // speed we actually move at, otherwise a 1 m/s scripted walk was broadcast as a run.
            var moveFlags = AiPathActorFlags ?? Owner.GetRealMovementFlags(moveSpeed);
            var stepDistance = moveSpeed * delta.Milliseconds / 1000.0;
            Owner.Owner.MoveTowards(TargetPosition, (float)stepDistance, moveFlags, arrivalRadius,
                authoredPathZ: true, speedMetersPerSecond: (float)moveSpeed);

            // Move the idle "home" location along with the path, so it doesn't immediately trigger a return to home state when going into combat
            Owner.IdlePosition = Owner.Owner.Transform.World.Position;
        }

        return HasUnhandledPathMovementData();
    }

    public bool HasUnhandledPathMovementData()
    {
        return AiPathPointsRemaining.Count > 0 || AiPathLooping || TargetPosition != Vector3.Zero;
    }

    public bool HasPathMovementData()
    {
        return AiPathPointsRemaining.Count > 0 || (AiPathLooping && AiPathPoints.Count > 0);
    }
}
