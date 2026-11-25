using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCTeamAreaInvitedPacket(uint remainingMilliseconds, bool accepted) : GamePacket(SCOffsets.SCTeamAreaInvitedPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(remainingMilliseconds);
        stream.Write(accepted);
        return stream;
    }
}
