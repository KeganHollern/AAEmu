using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSChangeSlaveNamePacket() : GamePacket(CSOffsets.CSChangeSlaveNamePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var tlId = stream.ReadUInt16();
        var name = stream.ReadString();
        var owner = Connection.ActiveChar;
        owner?.ParentWorld?.SlaveManager.RenameVehicle(owner, tlId, name);
    }
}
