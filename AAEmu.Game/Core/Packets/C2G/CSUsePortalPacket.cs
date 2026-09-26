using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSUsePortalPacket() : GamePacket(CSOffsets.CSUsePortalPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        var onlyMyPortal = stream.ReadByte();
        if (onlyMyPortal > 1 || stream.LeftBytes != 0)
            return;

        Logger.Debug("UsePortal, ObjId: {0}, OnlyMyPortal: {1}", objId, onlyMyPortal);

        PortalManager.UsePortal(Connection.ActiveChar, objId, onlyMyPortal != 0);
    }
}
