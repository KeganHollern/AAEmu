namespace AAEmu.Game.Models.Game.Skills;

/// <summary>A weapon's current attack cycle, independent of the other hand.</summary>
internal sealed class AutoAttackCycle
{
    private DateTime _cycleStart;

    internal bool Enabled { get; private set; }
    internal TimeSpan Interval { get; private set; }
    internal DateTime DueTime => _cycleStart + Interval;

    internal void Refresh(DateTime now, double milliseconds, bool enabled = true)
    {
        if (enabled && !Enabled)
            _cycleStart = now;
        Enabled = enabled;
        Interval = TimeSpan.FromMilliseconds(milliseconds);
    }

    internal bool TryConsume(DateTime now)
    {
        if (!Enabled || now < DueTime)
            return false;

        // A late callback consumes one attempt. It never catches up old swings.
        _cycleStart = now;
        return true;
    }
}
