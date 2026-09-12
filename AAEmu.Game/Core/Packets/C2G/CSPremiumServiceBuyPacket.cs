using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSPremiumServiceBuyPacket() : GamePacket(CSOffsets.CSPremiumServiceBuyPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.Count - stream.Pos != sizeof(int))
            return;
        _ = stream.ReadInt32();
        // No authored premium product catalog exists for this deployment.
        Connection.SendPacket(new SCErrorMsgPacket(ErrorMessageType.PremiumServiceBuyFail, 0, true));
    }
}
