using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Managers.TowerDefense;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSInstanceLoadedPacket() : GamePacket(CSOffsets.CSInstanceLoadedPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // Empty struct
        // TODO Debug

        Connection.SendPacket(new SCUnitStatePacket(Connection.ActiveChar));
        Connection.SendPacket(new SCDetailedTimeOfDayPacket(
            TimeManager.Instance.GetTime,
            TimeManager.Instance.ClientSpeed));

        Connection.ActiveChar.DisabledSetPosition = false;
        TowerDefenseManager.SendSnapshotIfAvailable(Connection.ActiveChar);

        Logger.Debug("InstanceLoaded.");
    }
}
