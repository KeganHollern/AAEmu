using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSUpdateNationalTaxRatePacket() : GamePacket(CSOffsets.CSUpdateNationalTaxRatePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var id = stream.ReadUInt16();
        var taxRate = stream.ReadInt32();

        Connection?.ActiveChar?.SendMessage("National tax changes are not available. Territories are unclaimed.");

    }
}
