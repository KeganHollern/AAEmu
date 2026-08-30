using AAEmu.Game.Models.Game.World;
using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.Game.Models.Game.TowerDefs;

public enum TowerDefenseOccurrenceStatus
{
    Scheduled,
    Starting,
    FirstWaveDelay,
    StepActive,
    StepTransition,
    Succeeded,
    TimedOut,
    Failed,
    Cancelled,
    Cleaning,
    Ended
}

public sealed class TowerDefenseOccurrence
{
    public string OccurrenceKey { get; init; }
    public TowerDefenseEventManifest Manifest { get; init; }
    public TowerDef Definition { get; init; }
    public TowerDefenseSiteManifest Site { get; init; }
    public WorldInstance World { get; init; }
    public long ScheduledDayOrdinal { get; init; }
    public DateTimeOffset ScheduledAtUtc { get; init; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset StepEnteredAtUtc { get; set; }
    public DateTimeOffset HardDeadlineUtc { get; set; }
    public TowerDefenseOccurrenceStatus Status { get; set; }
    public int Generation { get; set; }
    public int CurrentStepOrdinal { get; set; } = -1;
    public uint EventZoneId { get; set; }
    public uint ZoneGroupId { get; set; }
    public uint TargetObjId { get; set; }
    public bool Announced { get; set; }
    public bool TimerCriterionComplete { get; set; }
    public HashSet<uint> CountedVictims { get; } = [];
    public HashSet<uint> CountedTerminalVictims { get; } = [];
    public Dictionary<uint, TowerDefenseObjectiveProgress> Objectives { get; } = [];
    public TowerDefenseObjectiveProgress TerminalObjective { get; set; }
    public List<GameTask> ScheduledTasks { get; } = [];
    public string DefinitionHash { get; set; }
    public string TerminalReason { get; set; }
}

public sealed record TowerDefenseObjectiveProgress(uint TargetId, uint Required, uint Current)
{
    public TowerDefenseObjectiveProgress Increment() => this with { Current = Math.Min(Required, Current + 1) };
}
