using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.World.Interactions;

public class RecoverItem : IWorldInteraction
{
    internal const uint CollectSkillId = 11361;

    internal static bool IsDefaultFurnitureAction(Character player, Doodad doodad, DoodadFunc func)
    {
        // The r208022 action reads the placed item's template and compares the placing character's ID.
        return func.SkillId == 0 && func.FuncType == nameof(DoodadFuncRecoverItem) &&
            doodad.OwnerType == DoodadOwnerType.Housing && doodad.OwnerId == player.Id &&
            doodad.ItemId != 0 && doodad.ItemTemplateId != 0 &&
            HousingDecorationRules.GetDecoration(doodad, HousingGameData.Instance) != null;
    }

    public void Execute(BaseUnit caster, SkillCaster casterType, BaseUnit target, SkillCastTarget targetType,
        uint skillId, uint doodadId, DoodadFuncTemplate objectFunc = null)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            ExecuteLocked(caster, target, skillId);
        }
    }

    private static void ExecuteLocked(BaseUnit caster, BaseUnit target, uint skillId)
    {
        // check if you are equipped with a backpack or glider
        var hasBackPack = !((Character)caster).Inventory.CanReplaceGliderInBackpackSlot();

        if (target is Doodad doodad && doodad.AllowRemoval() && !hasBackPack)
        {
            // Get Funcs for current doodad phase
            var funcs = DoodadManager.Instance.GetFuncsForGroup(doodad.FuncGroupId);
            // Check if it contains a DoodadRecoverItem func
            foreach (var func in funcs)
            {
                var template = DoodadManager.Instance.GetFuncTemplate(func.FuncId, func.FuncType);
                if (template is DoodadFuncRecoverItem doodadFuncRecoverItemTemplate)
                {
                    // The phase or owner can change during the one-second Collect cast.
                    if (skillId == CollectSkillId && doodad.OwnerType == DoodadOwnerType.Housing &&
                        func.SkillId != CollectSkillId && !IsDefaultFurnitureAction((Character)caster, doodad, func))
                        continue;
                    if (!DoodadPermissionRules.Demand(caster, doodad, func.PermId))
                    {
                        return;
                    }
                    // Delete only after the item transfer succeeds.
                    if (doodadFuncRecoverItemTemplate.TryRecover((Character)caster, doodad))
                        doodad.Delete();
                    return;
                }
            }
        }

        // Something wasn't found or is invalid, so cancel whatever we're doing
        ((Unit)caster).SendErrorMessage(ErrorMessageType.FailedToUseItem);
        caster.InterruptSkills();
    }
}
