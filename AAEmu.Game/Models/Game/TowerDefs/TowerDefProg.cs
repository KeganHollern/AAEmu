namespace AAEmu.Game.Models.Game.TowerDefs;

public class TowerDefProg
{
    public uint Id { get; set; }
    public TowerDef TowerDef { get; set; }
    public uint StepOrdinal { get; internal set; }
    public string Msg { get; set; } = string.Empty;
    public float CondToNextTime { get; set; }
    public bool CondCompByAnd { get; set; }

    public List<TowerDefProgKillTarget> KillTargets { get; } = [];
    public List<TowerDefProgSpawnTarget> SpawnTargets { get; } = [];
}
