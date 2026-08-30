namespace AAEmu.Game.Models.Game.TowerDefs;

public sealed record TowerDefenseSpawnToken(
    string OccurrenceKey,
    string EventKey,
    string SiteKey,
    int Generation,
    int StepOrdinal,
    string ActionKey,
    uint CreatorObjId = 0,
    bool DespawnOnCreatorDeath = false);
