using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSaveUIDataPacket() : GamePacket(CSOffsets.CSSaveUIDataPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var uiDataType = stream.ReadUInt16();
        var id = stream.ReadUInt32();
        var data = stream.ReadString();

        Connection.ActiveChar.SetOption(uiDataType, data);
        // Write through to the database: UI data is client-authoritative state
        // (quest tracker, keybinds); losing it on an abrupt server stop showed
        // up as "all my quests are unchecked" after reconnecting (issue #28).
        Connection.ActiveChar.SaveOption(uiDataType);
    }
}
