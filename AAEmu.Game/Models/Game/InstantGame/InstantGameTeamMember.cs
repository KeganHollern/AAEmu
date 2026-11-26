using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame.Static;

namespace AAEmu.Game.Models.Game.InstantGame;

public class InstantGameTeamMember : PacketMarshaler
{
    private const uint VictoryBadge = 1u << 0;
    private const uint TopPlayerKillsBadge = 1u << 2;
    private const uint TopNpcKillsBadge = 1u << 3;
    private const uint DefeatBadge = 1u << 4;

    public Character Character { get; set; }
    public int Score { get; set; }
    public int Bonus { get; set; }
    public uint BonusSet { get; set; }
    public ushort Killstreak { get; set; }
    public ushort Kills { get; set; }
    internal int EnemyNpcKills { get; set; }
    public ushort Assists { get; set; }
    public ushort Deaths { get; set; }

    public InstantGameTeamResult Corps { get; set; }

    internal void SetMatchHonor(GameRuleSet rule, bool leadsPlayerKills, bool leadsNpcKills)
    {
        Bonus = Corps.State == VictoryState.Win ? rule.WinnerHonor : rule.LoserHonor;
        BonusSet &= ~(TopPlayerKillsBadge | TopNpcKillsBadge);
        if (leadsPlayerKills && rule.TopPlayerKillsHonor > 0)
        {
            Bonus += rule.TopPlayerKillsHonor;
            BonusSet |= TopPlayerKillsBadge;
        }
        if (leadsNpcKills && rule.TopNpcKillsHonor > 0)
        {
            Bonus += rule.TopNpcKillsHonor;
            BonusSet |= TopNpcKillsBadge;
        }
    }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(Character.Id);
        stream.Write(Character.Name);
        stream.Write(Score);
        stream.Write(Bonus);
        var outcomeBadge = Corps?.State switch
        {
            VictoryState.Win => VictoryBadge,
            VictoryState.Lose => DefeatBadge,
            _ => 0u
        };
        stream.Write((BonusSet & ~(VictoryBadge | DefeatBadge)) | outcomeBadge);
        stream.Write(Killstreak);
        stream.Write(Kills);
        stream.Write(Assists);
        stream.Write(Deaths);
        return stream;
    }
}
