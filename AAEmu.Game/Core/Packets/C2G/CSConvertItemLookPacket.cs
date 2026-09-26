using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSConvertItemLookPacket() : GamePacket(CSOffsets.CSConvertItemLookPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var baseId = stream.ReadUInt64();
        var lookId = stream.ReadUInt64();
        Convert(Connection.ActiveChar, baseId, lookId);
    }

    internal static bool Convert(Character character, ulong baseId, ulong lookId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var item = character.Inventory.GetItemById(baseId) as EquipItem;
            var image = character.Inventory.GetItemById(lookId) as EquipItem;
            if (item == null || image == null || ReferenceEquals(item, image) ||
                item.OwnerId != character.Id || image.OwnerId != character.Id ||
                item.SlotType is not (SlotType.Inventory or SlotType.Equipment) || image.SlotType != SlotType.Inventory ||
                item.Template is not EquipItemTemplate { ItemLookConvert: { Id: > 0 } rule } ||
                image.Template is not EquipItemTemplate { ItemLookConvert: { } imageRule } ||
                rule.Id != imageRule.Id || rule.RequiredItemId == 0 || rule.RequiredItemCount <= 0 ||
                TradeReservation.GetReservedCount(item) != 0 || TradeReservation.GetReservedCount(image) != 0)
            {
                character.SendErrorMessage(ErrorMessageType.ItemLookConvertAsInvalidCombination);
                return false;
            }

            using var mutation = new InventoryMutation(ItemTaskType.ConvertItemLook);
            // Capture both source items and all powder stacks before publishing any change.
            // A later missing or reserved stack must restore every earlier debit.
            var needed = rule.RequiredItemCount;
            foreach (var powder in character.Inventory.Bag.Items.Where(candidate =>
                         candidate.TemplateId == rule.RequiredItemId && candidate != item && candidate != image)
                         .OrderBy(candidate => candidate.Slot).ToArray())
            {
                var amount = Math.Min(needed, powder.Count - TradeReservation.GetReservedCount(powder));
                if (amount <= 0)
                    continue;
                if (!mutation.TryConsume(character.Inventory.Bag, powder, amount))
                    break;
                needed -= amount;
                if (needed == 0)
                    break;
            }
            if (needed != 0)
            {
                character.SendErrorMessage(ErrorMessageType.NotEnoughRequiredItem, rule.RequiredItemId);
                return false;
            }
            if (!mutation.TryConsume(character.Inventory.Bag, image, 1) ||
                !mutation.TryChangeAppearance(item._holdingContainer, item, image.TemplateId))
            {
                character.SendErrorMessage(ErrorMessageType.ItemLookConvertAsInvalidCombination);
                return false;
            }
            return mutation.Complete();
        }
    }
}
