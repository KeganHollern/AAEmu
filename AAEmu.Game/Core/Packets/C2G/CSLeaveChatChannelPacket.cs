using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSLeaveChatChannelPacket() : GamePacket(CSOffsets.CSLeaveChatChannelPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var key = stream.ReadUInt64();
        UserChatWire.End(stream);
        ChatManager.Instance.UserChannels.Leave(Connection, key);
    }
}
