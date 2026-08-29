using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSNotifyInGameCompletedPacket() : GamePacket(CSOffsets.CSNotifyInGameCompletedPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        WorldManager.Instance.OnPlayerJoin(Connection.ActiveChar);
        // Action slots reset their visual state when the client leaves the loading screen.
        // Send the persisted cooldown snapshot after the client completes its initial world setup.
        Connection.SendPacket(new SCCooldownsPacket(Connection.ActiveChar.Cooldowns));
        Logger.Info($"NotifyInGameCompleted SubZoneId {Connection.ActiveChar.SubZoneId}, {Connection.ActiveChar?.Name} ({Connection.ActiveChar?.Id})");
    }
}
