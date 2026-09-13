using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Login;

namespace AAEmu.Game.Core.Packets.L2G;

public class LGPlayerReconnectPacket() : LoginPacket(LGOffsets.LGPlayerReconnectPacket)
{
    public override void Read(PacketStream stream)
    {
        var token = stream.ReadUInt32();
        ReconnectTokenManager.Instance.Acknowledge(token);
    }
}
