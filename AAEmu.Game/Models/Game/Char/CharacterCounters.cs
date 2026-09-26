using AAEmu.Game.Models.Game.Crime;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    public uint HostileFactionKills { get; set; }
    public uint HonorGainedInCombat { get; set; }
    public int ConsumedLaborPower { get; set; }
    public short DeadCount { get; set; }

    // Justice
    public int ArrestCount { get; set; }
    public int AcceptGuiltyCount { get; set; }
    public int AcceptTrialCount { get; set; }
    public int NotGuiltyCount { get; set; }
    public int GuiltyCount { get; set; }
    public int EvidenceReportedCount { get; set; }
    public int BotReportedCount { get; set; }
    public int ReportedAsBotCount { get; set; }
    /// <summary>Pending default sentence in minutes: positive minutes, -1 for a zero-minute case, 0 for no case.</summary>
    public int OfflineGuiltyTime { get; set; }
    public CourtRoomRegion OfflineGuiltyRegion { get; set; }
}
