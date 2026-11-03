using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Login;

namespace AAEmu.Game.Core.Packets.G2L;

public class GLRegisterGameServerPacket(string secretKey, byte gsId)
    : LoginPacket(GLOffsets.GLRegisterGameServerPacket)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(secretKey);
        stream.Write(gsId);
        // The registration envelope reserves this count, but one instance owns one ID.
        stream.Write(0);
        return stream;
    }
}
