using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSRepairPetItemsPacket() : GamePacket(CSOffsets.CSRepairPetItemsPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (!TryReadRequest(stream, out var npcId))
            return;
        var character = Connection.ActiveChar;
        var npc = character?.ParentWorld?.GetNpc(npcId);
        character?.RepairPets(npc);
    }

    internal static bool TryReadRequest(PacketStream stream, out uint npcId)
    {
        npcId = 0;
        if (stream.LeftBytes != 3)
            return false;
        npcId = stream.ReadBc();
        return true;
    }
}
