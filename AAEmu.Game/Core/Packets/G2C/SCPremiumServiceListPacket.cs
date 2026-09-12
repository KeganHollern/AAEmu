using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.CashShop;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>The disabled purchase service has an empty, complete product list.</summary>
public class SCPremiumServiceListPacket() : GamePacket(SCOffsets.SCPremiumServiceListPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(true); // isEnd
        stream.Write((byte)0); // product count
        // r208022 reads this fixed structure even when the product count is zero.
        stream.Write(new PremiumDetail { CName = string.Empty });
        stream.Write(0); // exchangeRatio
        return stream;
    }
}
