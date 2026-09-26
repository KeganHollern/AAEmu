using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSChallengeDuelPacket() : GamePacket(CSOffsets.CSChallengeDuelPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (!TryReadRequest(stream, out var challengedId) || Connection.ActiveChar == null)
            return;

        DuelManager.Instance.DuelRequest(Connection.ActiveChar, challengedId); // only to the enemy
    }
    internal static bool TryReadRequest(PacketStream stream, out uint challengedId)
    {
        challengedId = 0;
        if (stream.Count - stream.Pos != 4)
            return false;
        challengedId = stream.ReadUInt32();
        return challengedId != 0;
    }

}
