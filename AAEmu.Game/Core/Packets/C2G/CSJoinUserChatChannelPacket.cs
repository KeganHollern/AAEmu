using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSJoinUserChatChannelPacket() : GamePacket(CSOffsets.CSJoinUserChatChannelPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var name = UserChatWire.ReadString(stream, 48);
        var password = UserChatWire.ReadString(stream, 6);
        var create = stream.ReadByte();
        if (create > 1)
            throw new InvalidDataException("Invalid user channel create flag.");
        UserChatWire.End(stream);
        ChatManager.Instance.UserChannels.Join(Connection, name, password, create == 1);
    }
}
