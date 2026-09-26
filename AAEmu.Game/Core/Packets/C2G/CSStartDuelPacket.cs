using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSStartDuelPacket() : GamePacket(CSOffsets.CSStartDuelPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (!TryReadRequest(stream, out var challengerId, out var errorMessage) || Connection.ActiveChar == null)
            return;

        Logger.Warn("StartDuel, Id: {0}, ErrorMessage: {1}", challengerId, errorMessage);

        if (errorMessage != 0)
        {
            DuelManager.Instance.DuelCancel(Connection.ActiveChar, challengerId, (ErrorMessageType)errorMessage);
            return;
        }

        DuelManager.Instance.DuelAccepted(Connection.ActiveChar, challengerId);
    }
    internal static bool TryReadRequest(PacketStream stream, out uint challengerId, out short errorMessage)
    {
        challengerId = 0;
        errorMessage = 0;
        if (stream.Count - stream.Pos != 6)
            return false;
        challengerId = stream.ReadUInt32();
        errorMessage = stream.ReadInt16();
        return challengerId != 0 && errorMessage is 0 or 507;
    }

}
