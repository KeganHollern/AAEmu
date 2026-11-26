namespace AAEmu.Game.Models.Game.InstantGame;

public class GameRuleSet
{
    public uint Id { get; set; }
    public uint MinimumLevel { get; set; }
    public uint CorpsSize { get; set; }
    public uint Corps1FactionId { get; set; }
    public uint Corps2FactionId { get; set; }
    public int TimeOpening { get; set; }
    public int TimeEnding { get; set; }
    public int TimePlaying { get; set; }
    public TimeSpan ResurrectionDelay { get; set; }
    public int EnemyPlayerKillScore { get; set; }
    public int RankWinPoints { get; set; }
    public int RankLossPoints { get; set; }
    public int RankDrawPoints { get; set; }
    public int WinnerHonor { get; set; }
    public int LoserHonor { get; set; }
    public int TopPlayerKillsHonor { get; set; }
    public int TopNpcKillsHonor { get; set; }
    public int VictoryScore { get; set; }
    public uint BattlefieldId { get; set; }
    // TODO fill out the rest of the fields
}
