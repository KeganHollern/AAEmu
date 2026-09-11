using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Utils.Scripts;
using System.Text.Json;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSConsoleCmdUsedPacket() : GamePacket(CSOffsets.CSConsoleCmdUsedPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var cmd = stream.ReadString();

        Logger.Info("{EventName}: Account={ActorAccountId} Character={ActorCharacterId} Address={RemoteAddress} Command={ConsoleCommand}",
            "client.console.report", Connection.AccountId, Connection.ActiveChar?.Id ?? 0,
            Connection.Ip?.ToString() ?? "", JsonSerializer.Serialize(CommandAuditContext.Bound(cmd, 4096)));
    }
}
