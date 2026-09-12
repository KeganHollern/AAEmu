using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSPremiumServiceMsgPacket() : GamePacket(CSOffsets.CSPremiumServiceMsgPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.Count - stream.Pos != sizeof(int))
            return;
        _ = stream.ReadInt32();
        Connection.SendPacket(new SCErrorMsgPacket(ErrorMessageType.PremiumServiceBuyFail, 0, true));
    }
}
