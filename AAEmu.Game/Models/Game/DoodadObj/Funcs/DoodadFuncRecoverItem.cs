using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncRecoverItem : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Debug($"DoodadFuncRecoverItem({Id}) - Caster:{caster.Name} - DoodadOwner Template:{owner?.TemplateId} - SkillId:{skillId} - Nextphase:{nextPhase}");

        TryRecover(caster as Character, owner);
    }

    public bool TryRecover(Character character, Doodad owner)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (owner != null)
                SkillLaborBatch.For(character)?.TrackDoodad(owner);
            var recovered = TryRecoverLocked(character, owner);
            if (!recovered)
                SkillLaborBatch.For(character)?.Fail();
            return recovered;
        }
    }

    private static bool TryRecoverLocked(Character character, Doodad owner)
    {
        if (owner == null)
            return false;
        owner.ToNextPhase = false;
        if (character == null)
            return false;

        // Check property access before either recovery path changes an item.
        if (owner.OwnerType == DoodadOwnerType.Housing &&
            !(HousingManager.Instance.GetHouseById(owner.OwnerDbId)?.AllowedToInteract(character) ?? false))
        {
            character.SendErrorMessage(ErrorMessageType.InteractionPermissionDeny);
            return false;
        }

        var addedItem = false;
        var item = ItemManager.Instance.GetItemByItemId(owner.ItemId);
        if (owner.ItemId > 0)
        {
            if (item != null)
            {
                // A recoverable item stays in a System container until pickup succeeds.
                if (item._holdingContainer?.ContainerType != SlotType.System)
                {
                    character.SendErrorMessage(ErrorMessageType.InteractionRecoverParent);
                    return false;
                }

                if (ItemManager.Instance.IsAutoEquipTradePack(item.TemplateId))
                {
                    if (character.Inventory.TakeoffBackpack(ItemTaskType.RecoverDoodadItem, true))
                    {
                        if (character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.RecoverDoodadItem,
                            item,
                            (int)EquipmentItemSlot.Backpack))
                            addedItem = true;
                    }
                }
                else
                {
                    if (character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.RecoverDoodadItem, item))
                        addedItem = true;
                }
            }
        }
        else if (owner?.ItemTemplateId > 0)
        {
            addedItem = character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.RecoverDoodadItem, owner.ItemTemplateId, 1);
        }
        else
        {
            // No itemId was provided with the doodad, need to check what needs to be done with this
            Logger.Warn($"DoodadFuncRecoverItem: Doodad {owner?.ObjId} has no item information attached to it");
            character.SendErrorMessage(ErrorMessageType.FailedToUseItem);
        }

        if (addedItem && item != null && item._holdingContainer.ContainerType == SlotType.Equipment)
            character.BroadcastPacket(new SCUnitEquipmentsChangedPacket(character.ObjId, (byte)item.Slot, item), false);

        if (addedItem && owner != null)
        {
            // remove the old reference
            owner.ItemId = 0;
            owner.ItemTemplateId = 0;
        }

        owner.ToNextPhase = addedItem;
        return addedItem;
    }
}
