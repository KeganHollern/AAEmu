using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// Player does a die roll response
/// </summary>
public class CSLootDicePacket() : GamePacket(CSOffsets.CSLootDicePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var lootId = stream.ReadUInt64();
        var choice = stream.ReadByte();
        if (stream.LeftBytes != 0 || choice > 1)
            return;

        var itemIndex = (ushort)lootId;
        var lootOwnerType = (LootOwnerType)(ushort)(lootId >> 16);
        var lootOwnerObjId = (uint)(lootId >> 32);
        var rollRequest = choice == 1;

        BaseUnit lootOwner = null;
        switch (lootOwnerType)
        {
            case LootOwnerType.Npc:
                lootOwner = Connection.ActiveChar.ParentWorld.GetNpc(lootOwnerObjId);
                break;
            case LootOwnerType.Doodad:
                lootOwner = Connection.ActiveChar.ParentWorld.GetDoodad(lootOwnerObjId);
                break;
        }

        if (lootOwner == null)
        {
            Logger.Warn($"CSLootDice, LootOwner not found: {lootOwnerType}:{lootOwnerObjId} by {Connection.ActiveChar.Name}");
            return;
        }
        lootOwner.LootingContainer.DoPlayerRoll(Connection.ActiveChar, itemIndex, rollRequest);
    }
}
