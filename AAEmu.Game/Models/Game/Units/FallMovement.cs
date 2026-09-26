using System.Diagnostics;

namespace AAEmu.Game.Models.Game.Units;

/// <summary>
/// Observes accepted world heights. Contact is server terrain or the native landing
/// report for geometry without a server actor-contact query. The report's numeric
/// velocity is never a damage authority. This is not a vertical movement validator.
/// </summary>
internal sealed class FallMovement
{
    // r208022 position Z quantization is 4196 / 2^22 metres. Allow both endpoints
    // to round independently. This is a numeric tolerance, not a collision radius.
    internal const float PositionTolerance = 2 * 4196f / 4194304;
    private readonly object _lock = new();
    private bool _hasSample;
    private float _lastHeight;
    private double _lastTime;
    private bool _lastGrounded;
    private bool _falling;
    private float _startHeight;
    private double _peakSpeed;

    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal void Reset()
    {
        lock (_lock)
        {
            _hasSample = false;
            _falling = false;
            _peakSpeed = 0;
        }
    }

    internal ushort Observe(float height, double time, bool grounded, bool reportedLanding)
    {
        lock (_lock)
        {
            if (!float.IsFinite(height) || !double.IsFinite(time))
                return 0;

            ushort impact = 0;
            if (_hasSample)
            {
                var descent = _lastHeight - height;
                // Continuous terrain contact is walking down a slope or stairs.
                // A stationary airborne packet does not end or reset a fall.
                if (_lastGrounded && grounded && !reportedLanding)
                {
                    _falling = false;
                    _peakSpeed = 0;
                }
                else if (descent > PositionTolerance)
                {
                    if (!_falling)
                    {
                        _falling = true;
                        _startHeight = _lastHeight;
                    }
                    if (time > _lastTime)
                        _peakSpeed = Math.Max(_peakSpeed, descent / (time - _lastTime));
                }
                else if (_falling && height > _startHeight)
                {
                    // A jump or server displacement can rise before a later descent.
                    _startHeight = height;
                    _peakSpeed = 0;
                }

                if (_falling && (grounded || reportedLanding))
                {
                    impact = EncodeImpact(_startHeight - height, _peakSpeed);
                    _falling = false;
                    _peakSpeed = 0;
                }
            }

            _hasSample = true;
            _lastHeight = height;
            _lastTime = time;
            _lastGrounded = grounded;
            return impact;
        }
    }

    internal static ushort EncodeImpact(float descent, double observedSpeed)
    {
        if (!float.IsFinite(descent) || !double.IsFinite(observedSpeed) || descent <= 0 || observedSpeed <= 0)
            return 0;
        // Native CActor::UpdateFallDamage caps peak downward speed by sqrt(drop * 40).
        // Native actor.fallVel maps 0..128 m/s onto the complete ushort range.
        var speed = Math.Min(observedSpeed, Math.Sqrt(descent * 40d));
        return (ushort)Math.Round(Math.Clamp(speed / 128d, 0, 1) * ushort.MaxValue,
            MidpointRounding.AwayFromZero);
    }
}
