using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSBuyPriestBuffPacket() : GamePacket(CSOffsets.CSBuyPriestBuffPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (!TryReadRequest(stream, out var offerId, out var npcId) || Connection.ActiveChar is not { } character)
            return;
        character.BuyPriestBuff(offerId, character.ParentWorld?.GetNpc(npcId));
    }

    internal static bool TryReadRequest(PacketStream stream, out uint offerId, out uint npcId)
    {
        offerId = npcId = 0;
        if (stream.Count - stream.Pos != 7)
            return false;
        offerId = stream.ReadUInt32();
        npcId = stream.ReadBc();
        return true;
    }
}
