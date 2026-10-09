using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

// r208022 adds this signed delta to the owner's current mate mileage.
public class SCMileageChangedPacket(uint objId, int mileageDelta) : GamePacket(SCOffsets.SCMileageChangedPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.WriteBc(objId);
        stream.Write(mileageDelta);
        return stream;
    }
}
