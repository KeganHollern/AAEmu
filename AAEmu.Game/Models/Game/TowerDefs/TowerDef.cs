namespace AAEmu.Game.Models.Game.TowerDefs;

public class TowerDef
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StartMsg { get; set; } = string.Empty;
    public string EndMsg { get; set; } = string.Empty;
    public float TimeOfDay { get; set; }
    public float FirstWaveAfter { get; set; }
    public uint? TargetNpcSpawnerId { get; set; }
    public uint? KillNpcId { get; set; }
    public uint? KillNpcCount { get; set; }
    public float ForceEndTime { get; set; }
    public uint TimeOfDayDayInterval { get; set; }
    public string TitleMsg { get; set; } = string.Empty;
    public uint? MilestoneId { get; set; }
    public bool IsValid { get; internal set; } = true;

    public List<TowerDefProg> Progs { get; } = [];
}
