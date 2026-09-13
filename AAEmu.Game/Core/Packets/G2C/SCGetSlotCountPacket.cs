using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

// The client adds this count to the default limit supplied by Login.
public class SCGetSlotCountPacket(byte expandedSlots) : GamePacket(SCOffsets.SCGetSlotCountPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(expandedSlots);
        return stream;
    }
}
