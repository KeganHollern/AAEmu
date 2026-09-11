using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.Game.Models.Game.Skills;

internal static class SkillItemSource
{
    internal static bool CanUse(Item item, uint casterObjectId, SkillItem source, SkillTemplate skill,
        SkillCastTarget target, SkillObject skillObject)
    {
        if (item?.Template == null || skill == null || source.ObjId != casterObjectId ||
            source.ItemId != item.Id || source.ItemTemplateId != item.TemplateId)
            return false;

        if (skill.Id != 0 && skill.Id == item.Template.UseSkillId)
            return true;

        // r208022 SavePortal (3938f7d0) and RenamePortal (3938f950) both find book 4045,
        // then start 11215/16841 with self target and SavePortalInfo. The book's normal
        // UseSkillId is 11216. Binding status never authorizes an unrelated skill.
        return item.TemplateId == 4045 && item.Template.UseSkillId == 11216 &&
            skill.TargetType == SkillTargetType.Self &&
            target is SkillCastUnitTarget && target.ObjId == casterObjectId &&
            skillObject is SkillObjectSavePortalInfo portal &&
            (skill.Id == 11215 && portal.Id == 0 || skill.Id == 16841 && portal.Id > 0);
    }
}
