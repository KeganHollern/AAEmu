using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class BuffSteal : SpecialEffectAction
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
        if (caster is not Unit thief || target is not Unit victim || thief == victim ||
            thief.IsDead || victim.IsDead || value1 <= 0 || !thief.CanAttack(victim) ||
            skill?.SkillMissed(victim.ObjId) == true)
            return;

        using var duelEffect = DuelManager.Instance.EnterEffect(thief, victim, new EffectSource(skill));
        if (!duelEffect.Allowed)
            return;

        // The authored skill effect owns the chance roll. Value1 is the number to transfer.
        foreach (var buff in BuffSpecialEffectRules.GetRemovableGoodBuffs(victim, time)
                     .Where(buff => !thief.Buffs.CheckBuffImmune(buff.Template.Id) &&
                         (buff.Template.RequireBuffId == 0 || thief.Buffs.CheckBuff(buff.Template.RequireBuffId)))
                     .Take(value1))
        {
            var remaining = BuffSpecialEffectRules.RemainingDuration(buff, time);
            if (buff.Duration > 0 && remaining == 0)
                continue;
            var stolen = new Buff(thief, thief, casterObj, buff.Template, null, time)
            {
                AbLevel = buff.AbLevel,
                Charge = buff.Charge
            };
            if (victim.Buffs.TryConsumeActiveBuff(buff))
                thief.Buffs.AddBuff(stolen, forcedDuration: remaining);
        }
    }
}
