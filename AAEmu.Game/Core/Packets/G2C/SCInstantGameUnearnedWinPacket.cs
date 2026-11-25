using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

// r208022 shows the time to a default win when the opposing side leaves.
public class SCInstantGameUnearnedWinPacket(uint remainingMilliseconds) : GamePacket(SCOffsets.SCInstantGameUnearnedWinPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(remainingMilliseconds);
        return stream;
    }
}
