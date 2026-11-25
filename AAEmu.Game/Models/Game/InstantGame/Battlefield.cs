using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.InstantGame;

public class Battlefield
{
    public uint Id { get; set; }
    public uint ZoneKey { get; set; }
    public BattlefieldSpawns Spawns { get; set; }
    public GameRuleSet RuleSet { get; set; }

    internal bool HasMatchData =>
        ZoneKey != 0 && RuleSet is { CorpsSize: > 0, Corps1FactionId: > 0, Corps2FactionId: > 0 } &&
        RuleSet.Corps1FactionId != RuleSet.Corps2FactionId &&
        IsTeamSpawn(Spawns?.Corps1Spawn) && IsTeamSpawn(Spawns?.Corps2Spawn);

    private bool IsTeamSpawn(Point point) =>
        point != null && point.ZoneId == ZoneKey &&
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
