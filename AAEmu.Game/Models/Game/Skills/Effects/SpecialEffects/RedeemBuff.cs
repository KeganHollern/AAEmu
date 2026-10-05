using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class RedeemBuff : SpecialEffectAction
{
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
        if (caster is not Unit owner || target != caster || owner.IsDead)
            return;

        var amount = BuffSpecialEffectRules.RollResourceAmount(owner.MaxMp, value1, value2, value3);
        foreach (var buff in BuffSpecialEffectRules.GetRemovableGoodBuffs(owner, time))
        {
            if (!owner.Buffs.TryConsumeActiveBuff(buff))
                continue;

            new RestoreManaEffect { UseFixedValue = true, FixedMin = amount, FixedMax = amount }
                .Apply(owner, casterObj, owner, targetObj, castObj, new EffectSource(skill), skillObject, time);
            return;
        }

        // Absorb Effect's next authored effect grants Inspired. It needs a consumed buff.
        if (skill != null)
            skill.Cancelled = true;
    }
}
