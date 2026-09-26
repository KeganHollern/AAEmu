using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCCastingDelayedPacket(ushort skillTlId, ushort plotTlId, uint delayMilliseconds)
    : GamePacket(SCOffsets.SCCastingDelayedPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(skillTlId);
        stream.Write(plotTlId);
        stream.Write(delayMilliseconds);
        return stream;
    }
}
