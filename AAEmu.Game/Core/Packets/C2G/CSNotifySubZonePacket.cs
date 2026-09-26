using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSNotifySubZonePacket() : GamePacket(CSOffsets.CSNotifySubZonePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var subZoneId = stream.ReadUInt32();
        Connection.ActiveChar?.Portals.NotifySubZone(subZoneId);
    }
}
