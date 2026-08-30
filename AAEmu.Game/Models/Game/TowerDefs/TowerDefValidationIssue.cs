namespace AAEmu.Game.Models.Game.TowerDefs;

public enum TowerDefValidationSeverity
{
    Warning,
    Error
}

public sealed record TowerDefValidationIssue(
    TowerDefValidationSeverity Severity,
    string Source,
    uint ChildId,
    uint? TowerDefId,
    string Message);
