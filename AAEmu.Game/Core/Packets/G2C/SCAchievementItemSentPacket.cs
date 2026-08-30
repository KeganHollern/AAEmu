using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCAchievementItemSentPacket(uint achievementId, bool byMail, uint itemTemplateId)
    : GamePacket(SCOffsets.SCAchievementItemSentPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(achievementId);
        stream.Write(byMail);
        stream.Write(itemTemplateId);

        return stream;
    }
}
