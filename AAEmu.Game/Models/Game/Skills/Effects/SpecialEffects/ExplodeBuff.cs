using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ExplodeBuff : SpecialEffectAction
{
    internal EffectSource Source { get; set; }

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
        if (caster is not Unit attacker || target is not Unit victim || attacker == victim ||
            victim.IsDead || !attacker.CanAttack(victim) || skill?.SkillMissed(victim.ObjId) == true)
            return;

        var source = Source ?? new EffectSource(skill);
        // The generic buff trigger currently omits this context from EffectSource.
        // The originating CastBuff retains it even after the duel has finished.
        if (castObj is CastBuff castBuff)
            source.DuelContext ??= castBuff.Buff.DuelContext;
        using var duelEffect = DuelManager.Instance.EnterEffect(attacker, victim, source);
        if (!duelEffect.Allowed)
            return;

        var amount = BuffSpecialEffectRules.RollResourceAmount(victim.MaxHp, value1, value2, value3);
        foreach (var buff in BuffSpecialEffectRules.GetRemovableGoodBuffs(victim, time))
        {
            if (!victim.Buffs.TryConsumeActiveBuff(buff))
                continue;

            new DamageEffect
            {
                DamageType = DamageType.Magic,
                UseFixedDamage = true,
                FixedMin = amount,
                FixedMax = amount,
                WeaponSlotId = -1
            }.Apply(attacker, casterObj, victim, targetObj, castObj, source, skillObject, time);
            return;
        }
    }
}
