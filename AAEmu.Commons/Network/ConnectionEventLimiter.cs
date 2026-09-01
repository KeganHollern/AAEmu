namespace AAEmu.Commons.Network;

/// <summary>
/// Provides a small, thread-safe event budget for one network connection.
/// </summary>
public sealed class ConnectionEventLimiter
{
    public const int DefaultLimit = 3;

    private int _remaining;

    public ConnectionEventLimiter(int limit = DefaultLimit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The event limit must be positive.");

        _remaining = limit;
    }

    /// <summary>
    /// Returns <see langword="true"/> while this connection still has event budget remaining.
    /// </summary>
    public bool TryConsume()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _remaining);
            if (remaining == 0)
                return false;

            if (Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining)
                return true;
        }
    }
}
