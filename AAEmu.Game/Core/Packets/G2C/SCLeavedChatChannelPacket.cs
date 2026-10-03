using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCLeavedChatChannelPacket(ulong key)
    : GamePacket(SCOffsets.SCLeavedChatChannelPacket, 1)
{
    public SCLeavedChatChannelPacket(ChatType type, short subType, FactionsEnum factionId)
        : this((ushort)type | ((ulong)(ushort)subType << 16) | ((ulong)(uint)factionId << 32)) { }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(key);
        return stream;
    }
}
