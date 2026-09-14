using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.Game.Models.Game.Skills;

internal static class PriestSkillAuthorization
{
    internal static bool CanUse(Character character, SkillTemplate skill, SkillCaster source, SkillCastTarget target)
    {
        // r208022 X2Unit.RecoverExp requests exactly this Unit/self skill.
        // Full recovery scrolls use a separate, owned-item route.
        if (character == null || skill?.Id != 17063 || source is not SkillCasterUnit ||
            source.ObjId != character.ObjId || target is not SkillCastUnitTarget || target.ObjId != character.ObjId ||
            !skill.Effects.Any(effect => effect.Template is
                RecoverExpEffect { NeedPriest: true, NeedLaborPower: true, NeedMoney: false }))
            return false;

        // Keep the recovery effect's 10-metre priest range. Recheck live object identity here.
        return WorldManager.GetAround<Npc>(character, 10f).Any(npc => npc.Template?.Priest == true && !npc.IsDead &&
            ServiceInteraction.CanReach(character, npc, 10f));
    }
}
