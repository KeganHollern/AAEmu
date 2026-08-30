namespace AAEmu.Game.Models.Game.TowerDefs;

public class TowerDefProgKillTarget
{
    public uint Id { get; set; }
    public TowerDefProg TowerDefProg { get; set; }
    public uint KillTargetId { get; set; }
    public TowerDefTargetType KillTargetType { get; set; }
    public string RawKillTargetType { get; set; } = string.Empty;
    public uint KillCount { get; set; }
}
