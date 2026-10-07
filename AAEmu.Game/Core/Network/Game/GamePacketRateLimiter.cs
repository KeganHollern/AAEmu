namespace AAEmu.Game.Core.Network.Game;

/// <summary>Counts complete wire packets, including unknown opcodes, in monotonic one-second windows.</summary>
internal sealed class GamePacketRateLimiter(int limit, TimeProvider timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _windowStart;
    private int _count;

    // The connection session lock serializes packet decoding.
    internal bool TryConsume()
    {
        var now = _timeProvider.GetTimestamp();
        if (_count == 0 || _timeProvider.GetElapsedTime(_windowStart, now) >= TimeSpan.FromSeconds(1))
        {
            _windowStart = now;
            _count = 0;
        }
        if (_count >= limit)
            return false;
        ++_count;
        return true;
    }
}
