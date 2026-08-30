using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.Game.Core.Managers.TowerDefense;

public static class TowerDefenseSchedule
{
    public static bool TryGetCrossedDay(
        WorldClockTick tick,
        TowerDefenseTriggerManifest trigger,
        out long scheduledDayOrdinal)
    {
        scheduledDayOrdinal = tick.Current.DayOrdinal;
        if (!string.Equals(trigger.Type, "TimeOfDay", StringComparison.OrdinalIgnoreCase) ||
            trigger.DayInterval == 0 ||
            !float.IsFinite(trigger.Hour) || trigger.Hour is < 0f or >= 24f ||
            tick.Current.SourceHours < tick.Previous.SourceHours ||
            (tick.IsManual && tick.Current.Hours < tick.Previous.Hours))
            return false;

        var previous = tick.Previous.Hours;
        var current = tick.Current.Hours;
        var crossed = previous <= current
            ? trigger.Hour > previous && trigger.Hour <= current
            : trigger.Hour > previous || trigger.Hour <= current;
        if (!crossed)
            return false;

        if (previous > current && trigger.Hour > previous)
            scheduledDayOrdinal--;

        return PositiveModulo(scheduledDayOrdinal, trigger.DayInterval) == trigger.DayPhase % trigger.DayInterval;
    }

    public static bool IsInsideCatchUpWindow(
        WorldClockSnapshot current,
        TowerDefenseTriggerManifest trigger,
        float clientSpeed)
    {
        if (trigger.CatchUpGraceSeconds == 0 || clientSpeed <= 0f)
            return false;
        var elapsedHours = current.Hours - trigger.Hour;
        if (elapsedHours < 0f)
            elapsedHours += 24f;
        return elapsedHours / clientSpeed <= trigger.CatchUpGraceSeconds;
    }

    private static uint PositiveModulo(long value, uint divisor)
    {
        var result = value % divisor;
        return (uint)(result < 0 ? result + divisor : result);
    }
}
