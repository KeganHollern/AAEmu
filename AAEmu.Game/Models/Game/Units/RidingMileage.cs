using System.Numerics;

namespace AAEmu.Game.Models.Game.Units;

/// <summary>
/// Counts whole horizontal metres between continuous, eligible riding samples.
/// Fractional metres survive stops and seat changes during this summon.
/// </summary>
internal sealed class RidingMileage
{
    private Vector3 _lastPosition;
    private DateTime? _lastObservedAt;
    private double _fractionalMetres;
    private double _distanceAllowance = InitialDistanceAllowance;

    private const double InitialDistanceAllowance = MovementValidation.MaxHorizontalSpeed * 0.1;

    internal void ResetMovement()
    {
        _lastObservedAt = null;
        _distanceAllowance = InitialDistanceAllowance;
    }

    internal int Observe(Vector3 previousPosition, Vector3 currentPosition, DateTime observedAt, bool eligible)
    {
        if (!eligible || !IsFinite(previousPosition) || !IsFinite(currentPosition))
        {
            ResetMovement();
            return 0;
        }

        var previousSampleAt = _lastObservedAt;
        var continuous = previousSampleAt.HasValue && previousPosition == _lastPosition;
        _lastPosition = currentPosition;
        _lastObservedAt = observedAt;
        if (!continuous)
        {
            _distanceAllowance = InitialDistanceAllowance;
            return 0;
        }

        var elapsed = (observedAt - previousSampleAt.Value).TotalSeconds;
        var deltaX = (double)currentPosition.X - previousPosition.X;
        var deltaY = (double)currentPosition.Y - previousPosition.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

        if (elapsed <= 0)
            return 0;

        // Allow delayed packets to consume saved movement time. The small burst
        // allowance is shared across packets, so packet frequency cannot create
        // a new speed tolerance for every request.
        _distanceAllowance = Math.Min(MovementValidation.MaxSingleMoveDistance,
            _distanceAllowance + MovementValidation.MaxHorizontalSpeed * elapsed);
        // The movement guard tolerates isolated displacements. Those moves may
        // synchronize the actor, but they do not automatically earn mileage.
        if (distance > MovementValidation.MaxSingleMoveDistance || distance > _distanceAllowance)
            return 0;
        _distanceAllowance -= distance;

        var total = _fractionalMetres + distance;
        var wholeMetres = (int)Math.Floor(total);
        _fractionalMetres = total - wholeMetres;
        return wholeMetres;
    }

    private static bool IsFinite(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
