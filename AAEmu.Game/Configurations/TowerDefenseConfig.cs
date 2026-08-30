namespace AAEmu.Game.Models;

public sealed class TowerDefenseConfig
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
    public bool AllowManualWhenDisabled { get; set; } = true;
    public string ManifestPath { get; set; } = "Data/TowerDefense";
}
