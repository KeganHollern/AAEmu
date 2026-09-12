using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCLootDiceSummaryPacket(LootOwnerType lootOwnerType, uint lootOwner, ushort itemIndex, Dictionary<Character, sbyte> diceList) : GamePacket(SCOffsets.SCLootDiceSummaryPacket, 1)
{
    // Native r208022 stores 50 character IDs followed by 50 signed dice results.
    internal const int MaximumRollers = 50;

    public override PacketStream Write(PacketStream stream)
    {
        if (diceList.Count > MaximumRollers)
            throw new ArgumentOutOfRangeException(nameof(diceList), "A loot summary cannot exceed 50 players.");

        stream.Write(itemIndex);
        stream.Write((ushort)lootOwnerType);
        stream.WriteBc(lootOwner);
        stream.Write((byte)0);
        stream.Write(diceList.Count);
        foreach (var (player, dice) in diceList.OrderBy(entry => entry.Key.Id))
        {
            stream.Write(player.Id);
            stream.Write(dice);
        }
        return stream;
    }
}
