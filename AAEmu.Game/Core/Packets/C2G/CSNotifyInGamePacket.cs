using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSNotifyInGamePacket() : GamePacket(CSOffsets.CSNotifyInGamePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // No data
    }

    public override void Execute()
    {
        // Native world/instance readiness can notify again after local-unit binding.
        // Reject the repeated spawn action without disconnecting that valid session.
        if (!Connection.IsClosed && Connection.IsAuthenticated && Connection.State == GameState.World &&
            Connection.ActiveChar?.AccountId == Connection.AccountId)
        {
            if (Connection.StateRejectionEvents.TryConsume())
                Logger.Warn("Rejected repeated NotifyInGame spawn on connection {ConnectionId}", Connection.Id);
            return;
        }
        if (!Connection.TryAdvanceWorldEntry(GameState.EnteringWorld, GameState.World))
        {
            Connection.Shutdown();
            return;
        }

        Connection.ActiveChar.IsOnline = true;

        Connection.ActiveChar.Spawn();

        // Joining channel 1 (shout) will automatically also join /lfg and /trade for that zone on the client-side
        // Back in 1.x /trade was zone based, not faction based
        ChatManager.Instance.GetZoneChat(Connection.ActiveChar.Transform.ZoneId).JoinChannel(Connection.ActiveChar); // shout, trade, lfg
        ChatManager.Instance.GetNationChat(Connection.ActiveChar.Race).JoinChannel(Connection.ActiveChar); // nation
        // ChatManager.Instance.GetTrialChat(Connection.ActiveChar)?.JoinChannel(Connection.ActiveChar); // trial
        ChatManager.Instance.GetFactionChat(Connection.ActiveChar.Faction.MotherId).JoinChannel(Connection.ActiveChar); // faction

        // TODO: Maybe move to spawn character?
        TeamManager.Instance.UpdateAtLogin(Connection.ActiveChar);
        Connection.ActiveChar.Expedition?.OnCharacterLogin(Connection.ActiveChar);

        Connection.ActiveChar.UpdateGearBonuses(null, null);

        TrialManager.Instance.HandlePlayerLogin(Connection.ActiveChar);
        Logger.Info($"NotifyInGame: {Connection.ActiveChar?.Name} ({Connection.ActiveChar?.Id})");
    }
}
