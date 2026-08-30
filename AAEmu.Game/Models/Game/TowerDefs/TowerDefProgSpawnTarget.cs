namespace AAEmu.Game.Models.Game.TowerDefs;

public class TowerDefProgSpawnTarget
{
    public uint Id { get; set; }
    public TowerDefProg TowerDefProg { get; set; }
    public uint SpawnTargetId { get; set; }
    public TowerDefTargetType SpawnTargetType { get; set; }
    public string RawSpawnTargetType { get; set; } = string.Empty;
    public bool DespawnOnNextStep { get; set; }
}
