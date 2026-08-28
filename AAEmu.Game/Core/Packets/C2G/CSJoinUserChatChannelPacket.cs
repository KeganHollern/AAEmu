using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSJoinUserChatChannelPacket() : GamePacket(CSOffsets.CSJoinUserChatChannelPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var name = stream.ReadString();
        _ = stream.ReadString();
        var create = stream.ReadBoolean();

        Logger.Debug("JoinUserChatChannel, Name: {0}, Create: {1}", name, create);
    }
}
