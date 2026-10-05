using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class FakeDeath : SpecialEffectAction
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
        if (target is not Unit unit || unit.IsDead || unit.Hp <= 0 ||
            unit is Npc { Despawned: true } or Npc { CombatRetired: true } ||
            !ReferenceEquals(unit.ParentWorld?.GetUnit(unit.ObjId), unit))
            return;

        // The zero-argument form is the combat escape used by Play Dead's tick.
        // Its buff owns the ragdoll presentation. This is not a real death.
        // The separate Downfall impact form (700, 70, 0, 0) is deferred to aaemu-cluster#635.
        if (value1 != 0 || value2 != 0 || value3 != 0 || value4 != 0)
            return;

        unit.RemoveIncomingThreat();
    }
}
