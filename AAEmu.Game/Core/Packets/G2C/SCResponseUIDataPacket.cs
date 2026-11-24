using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCResponseUIDataPacket(uint characterId, ushort uiDataType, string uiData)
    : GamePacket(SCOffsets.SCResponseUIDataPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(characterId);
        stream.Write(uiDataType);
        var bytes = CharacterUiData.GetClientBytes(uiDataType, uiData);
        stream.Write(bytes, true);
        stream.Write(bytes.Length + 1);
        return stream;
    }
}
