namespace AAEmu.Game.Models.Game.TowerDefs;

public readonly record struct WorldClockSnapshot(
    float Hours,
    long DayOrdinal,
    double SourceHours,
    DateTimeOffset ObservedAtUtc);

public readonly record struct WorldClockTick(
    WorldClockSnapshot Previous,
    WorldClockSnapshot Current,
    bool IsManual);
