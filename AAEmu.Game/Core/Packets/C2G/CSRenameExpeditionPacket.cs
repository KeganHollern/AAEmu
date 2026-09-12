using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSRenameExpeditionPacket() : GamePacket(CSOffsets.CSRenameExpeditionPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var id = stream.ReadUInt32();
        var nameLength = stream.ReadUInt16();
        if (nameLength > 128 || stream.LeftBytes != nameLength + 1)
            throw new InvalidDataException("Invalid expedition rename body length.");
        var name = Encoding.UTF8.GetString(stream.ReadBytes(nameLength));
        var isExpedition = stream.ReadByte();
        if (isExpedition > 1)
            throw new InvalidDataException("Invalid expedition rename flag.");

        ExpeditionManager.Instance.Rename(Connection.ActiveChar, (FactionsEnum)id, name, isExpedition == 1);
    }
}
