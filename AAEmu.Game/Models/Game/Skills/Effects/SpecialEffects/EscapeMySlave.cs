using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class EscapeMySlave : SpecialEffectAction
{
    internal const uint OwnerSkill = 22143;

    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int value1,
        int value2,
        int value3,
        int value4)
    {
        if (skill.Id == OwnerSkill && skill.EscapeVehicle != null && caster is Character owner)
        {
            if (owner.ParentWorld?.SlaveManager.MoveLandVehicle(owner, skill.EscapeVehicle, targetObj) != true)
            {
                skill.Cancelled = true;
                // The final placement can lose a race with recall after the skill fired.
                // The General skill has an individual cooldown and no shared cooldown tag.
                owner.ResetSkillCooldown(OwnerSkill, 0, false);
            }
            return;
        }

        if (caster is Character player && targetObj is SkillCastPositionTarget skillCastPositionTarget)
        {
            Logger.Debug($"Special effects: EscapeMySlave value1 {value1}, value2 {value2}, value3 {value3}, value4 {value4}");

            player.ParentWorld.SlaveManager.RidersEscape(player, skillCastPositionTarget);
        }
    }
}
