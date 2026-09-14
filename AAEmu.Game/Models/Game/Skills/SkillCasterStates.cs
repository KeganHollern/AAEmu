using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.SkillControllers;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills;

public static class SkillCasterStates
{
    public static SkillResult Check(Unit caster, SkillTemplate skill, out uint resultValue)
    {
        resultValue = 0;
        if (caster.IsDead)
        {
            if (!skill.SourceDead)
                return SkillResult.SourceDied;
        }
        else if (!skill.SourceAlive)
        {
            return SkillResult.SourceAlive;
        }

        var buffs = caster.Buffs.GetEffectsByType(typeof(BuffTemplate))
            .Where(buff => buff.InUse && !buff.IsEnded() && buff.GetTimeLeft() != 0)
            .ToArray();

        // r208022 SourceStun exempts stun only. Sleep and blank-minded remain independent.
        if ((!skill.SourceStun && buffs.Any(buff => buff.Template.Stun)) ||
            buffs.Any(buff => buff.Template.Sleep))
            return SkillResult.CannotCastInStun;

        var blankMinded = buffs.FirstOrDefault(buff => buff.Template.BlankMinded);
        if (blankMinded != null)
        {
            resultValue = blankMinded.Template.BuffId;
            return SkillResult.BlankMinded;
        }

        // Character fear movement is client-driven, so the active buff is authoritative here.
        var fear = buffs.FirstOrDefault(buff => buff.Template.SkillControllerId != 0 &&
            SkillManager.Instance.GetEffectTemplate(buff.Template.SkillControllerId, "SkillController") is
                SkillControllerTemplate { KindId: (uint)SkillControllerKind.Wandering });
        var controller = caster.ActiveSkillController;
        if (fear != null || controller is
            WanderingSkillController { State: SkillController.SCState.Running })
        {
            resultValue = fear?.Template.BuffId ?? controller.SourceBuffId;
            return SkillResult.BlankMinded;
        }

        if (skill.DamageTypeId == (uint)DamageType.Magic && buffs.Any(buff => buff.Template.Silence))
            return SkillResult.Silence;

        if (skill.DamageTypeId is (uint)DamageType.Melee or (uint)DamageType.Ranged &&
            buffs.Any(buff => buff.Template.Cripled))
            return SkillResult.Crippled;

        // The server enforces the pacifist caster rule before any hostile cast side effects.
        if (IsHostile(skill) && buffs.Any(buff => buff.Template.Pacifist))
            return SkillResult.NoPerm;

        return SkillResult.Success;
    }

    private static bool IsHostile(SkillTemplate skill)
    {
        return skill.TargetType == SkillTargetType.Hostile || skill.TargetRelation == SkillTargetRelation.Hostile ||
            skill.Effects.Any(effect => effect.NonFriendly && !effect.Friendly &&
                effect.ApplicationMethod is SkillEffectApplicationMethod.Target or SkillEffectApplicationMethod.SourceToPos);
    }
}
