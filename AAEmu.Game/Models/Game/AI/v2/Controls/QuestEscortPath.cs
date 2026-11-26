using System.Numerics;
using AAEmu.Game.Models.Game.AI.v2.Framework;

namespace AAEmu.Game.Models.Game.AI.v2.Controls;

/// <summary>Walks a guard's quest path once and retraces its visited waypoints when the quest releases it.</summary>
internal sealed class QuestEscortPath(NpcAi ai, IReadOnlyList<AiPathPoint> points)
{
    private readonly AiPathHandler _movement = new(ai)
    {
        AiPathLooping = false,
        AiPathPointsRemaining = new Queue<AiPathPoint>(points)
    };
    private volatile bool _returnRequested;
    private bool _returning;
    private bool _stopped;

    internal bool ReturnRequested => _returnRequested;

    internal void RequestReturn() => _returnRequested = true;

    // Only the normal AI tick advances movement or changes the waypoint queue.
    internal bool Follow(TimeSpan delta)
    {
        if (_returnRequested && !_returning)
        {
            var visited = points.Count - _movement.AiPathPointsRemaining.Count;
            if (_movement.TargetPosition != Vector3.Zero)
                visited--;
            _movement.TargetPosition = Vector3.Zero;
            _movement.AiPathPointsRemaining.Clear();
            for (var i = Math.Max(0, visited - 1); i >= 0; i--)
                _movement.AiPathPointsRemaining.Enqueue(points[i]);
            _returning = true;
            _stopped = false;
        }
        if (_movement.RunCurrentPath(delta))
            return false;
        if (!_stopped)
        {
            ai.Owner.StopMovement();
            _stopped = true;
        }
        return _returning;
    }
}
