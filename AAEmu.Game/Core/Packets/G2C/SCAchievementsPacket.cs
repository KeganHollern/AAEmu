using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Achievement;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCAchievementsPacket : GamePacket
{
    public const int MaxEntries = 50;

    private readonly AchievementInfo[] _achievements;

    public SCAchievementsPacket(IReadOnlyList<AchievementInfo> achievements)
        : base(SCOffsets.SCAchievementsPacket, 1)
    {
        ArgumentNullException.ThrowIfNull(achievements);
        if (achievements.Count > MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(achievements), $"A snapshot can contain at most {MaxEntries} achievements.");

        _achievements = achievements.ToArray();
    }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(_achievements.Length);
        foreach (var achievement in _achievements)
        {
            stream.Write(achievement.Id);
            stream.Write(achievement.Amount);
            stream.Write(achievement.Complete);
        }

        return stream;
    }
}
