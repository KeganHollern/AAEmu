using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Items.Loots;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSLootItemPacket() : GamePacket(CSOffsets.CSLootItemPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var lootId = stream.ReadUInt64();
        _ = stream.ReadInt32(); // Client item value; the server container determines the granted quantity.
        if (stream.LeftBytes != 0)
            return;

        var itemIndex = (ushort)lootId;
        var ownerType = (LootOwnerType)(ushort)(lootId >> 16);
        var ownerObjId = (uint)(lootId >> 32);

        var world = Connection.ActiveChar.ParentWorld;
        AAEmu.Game.Models.Game.Units.BaseUnit owner = ownerType switch
        {
            LootOwnerType.Npc => world.GetNpc(ownerObjId),
            LootOwnerType.Doodad => world.GetDoodad(ownerObjId),
            _ => null
        };

        owner?.LootingContainer.TryTakeLoot(Connection.ActiveChar, itemIndex, null, false);
    }
}
