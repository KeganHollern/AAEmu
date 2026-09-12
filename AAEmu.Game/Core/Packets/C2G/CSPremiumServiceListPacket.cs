using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSPremiumServiceListPacket() : GamePacket(CSOffsets.CSPremiumServiceListPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.Count == stream.Pos)
            Connection.SendPacket(new SCPremiumServiceListPacket());
    }
}
