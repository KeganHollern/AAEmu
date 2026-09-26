namespace AAEmu.Game.Models.Game.Skills;

// One cast or channel wait. Completion and a reaction claim the same window.
internal sealed class CastWindow(DateTime deadline, bool casting, bool channeling, bool delayable)
{
    private readonly object _lock = new();
    private DateTime _deadline = deadline;
    private bool _closed;
    public bool Casting { get; } = casting;
    public bool Channeling { get; } = channeling;
    internal bool Delayable => Casting && delayable;

    internal bool TryComplete(DateTime now, out TimeSpan remaining)
    {
        lock (_lock)
        {
            remaining = _deadline - now;
            if (_closed)
                return false;
            if (remaining > TimeSpan.Zero)
                return false;
            _closed = true;
            return true;
        }
    }

    internal bool TryCancel()
    {
        lock (_lock)
        {
            if (_closed)
                return false;
            _closed = true;
            return true;
        }
    }

    internal bool TryDelay(DateTime now, int milliseconds)
    {
        lock (_lock)
        {
            if (_closed || !Casting || !delayable || milliseconds <= 0 || now >= _deadline)
                return false;
            _deadline = _deadline.AddMilliseconds(milliseconds);
            return true;
        }
    }

    internal DateTime Deadline
    {
        get
        {
            lock (_lock)
                return _deadline;
        }
    }

    internal bool Active
    {
        get
        {
            lock (_lock)
                return !_closed;
        }
    }
}
