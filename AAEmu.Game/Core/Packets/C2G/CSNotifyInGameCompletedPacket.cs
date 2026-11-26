using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSNotifyInGameCompletedPacket() : GamePacket(CSOffsets.CSNotifyInGameCompletedPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (Connection.IsClosed || !Connection.IsAuthenticated || Connection.State != GameState.World ||
            Connection.ActiveChar?.AccountId != Connection.AccountId)
        {
            Connection.Shutdown();
            return;
        }

        if (Connection.TryCompleteWorldEntry())
            WorldManager.Instance.OnPlayerJoin(Connection.ActiveChar);
        // A cross-world arena transfer registers the player before the destination finishes loading.
        // Publish regional visibility after the client can receive the destination actors.
        lock (SaveManager.PersistenceSyncRoot)
        {
            var character = Connection.ActiveChar;
            if (character.IsVisible && character.Region == null && character.ParentWorld?.GetCharacterByObjId(character.ObjId) == character)
                WorldManager.Instance.AddVisibleObject(character);
        }
        // The client resets some native state when it leaves the loading screen.
        // Send persistent state snapshots after the client completes its initial world setup.
        Connection.SendPacket(new SCCooldownsPacket(Connection.ActiveChar.Cooldowns));
        Connection.ActiveChar.Achievements?.SendSnapshot();
        Logger.Info($"NotifyInGameCompleted SubZoneId {Connection.ActiveChar.SubZoneId}, {Connection.ActiveChar?.Name} ({Connection.ActiveChar?.Id})");
    }
}
