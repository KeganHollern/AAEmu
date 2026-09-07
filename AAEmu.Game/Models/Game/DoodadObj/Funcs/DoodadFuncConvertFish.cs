using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncConvertFish : DoodadFuncTemplate
{
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is not Character character)
            return;

        ErrorMessageType error;
        lock (SaveManager.PersistenceSyncRoot)
        {
            error = ValidateInteraction(character, owner, skillId);
            if (error == ErrorMessageType.NoErrorMessage)
                error = Convert(character);
        }
        // Failed exchanges have already restored the pack and released the prepared trophy.
        if (error != ErrorMessageType.NoErrorMessage)
            character.SendErrorMessage(error);
    }

    private ErrorMessageType ValidateInteraction(Character character, Doodad owner, uint skillId)
    {
        if (owner == null || owner.Despawn > DateTime.MinValue || owner.ParentWorld == null ||
            !ReferenceEquals(character.ParentWorld, owner.ParentWorld) ||
            !ReferenceEquals(owner.ParentWorld.GetDoodad(owner.ObjId), owner))
            return ErrorMessageType.NoInteractionAvailable;

        var skill = SkillManager.Instance.GetSkillTemplate(skillId);
        if (skill == null)
            return ErrorMessageType.NoInteractionAvailable;
        var function = DoodadManager.Instance.GetFunc(owner.FuncGroupId, skillId);
        if (function?.FuncType != nameof(DoodadFuncConvertFish) || function.FuncId != Id)
            return ErrorMessageType.NoInteractionAvailable;
        var distance = character.GetDistanceTo(owner, true);
        if (!float.IsFinite(distance) || distance < skill.MinRange || distance > skill.MaxRange)
            return ErrorMessageType.TooFarAway;
        return ErrorMessageType.NoErrorMessage;
    }

    private ErrorMessageType Convert(Character character)
    {
        var backpack = character.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack);
        if (backpack is not { Count: 1 } ||
            !ItemManager.Instance.TryGetFishConversion(Id, backpack.TemplateId, out var output))
            return ErrorMessageType.StoreBackpackNogoods;

        using var mutation = new InventoryMutation(ItemTaskType.Fishing);
        var trophy = FishDetailsGameData.Instance.CreateTrophy(backpack.TemplateId, output.ItemId);
        if (trophy == null)
            return ErrorMessageType.StoreBackpackNogoods;
        if (!mutation.TryAddCreated(trophy, character.Inventory.Bag))
            return ErrorMessageType.BagFull;
        if (!mutation.TryConsume(character.Equipment, backpack, 1))
            return ErrorMessageType.StoreBackpackNogoods;
        return mutation.Complete() ? ErrorMessageType.NoErrorMessage : ErrorMessageType.StoreBackpackNogoods;
    }
}
