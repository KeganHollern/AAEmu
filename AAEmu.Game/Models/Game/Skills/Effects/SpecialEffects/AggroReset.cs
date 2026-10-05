using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class AggroReset : SpecialEffectAction
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
        if (target is not Npc npc || npc.IsDead || npc.Hp <= 0 || npc.Despawned || npc.CombatRetired ||
            !ReferenceEquals(npc.ParentWorld?.GetUnit(npc.ObjId), npc))
            return;

        // User-approved server rule for the only r208022 argument set.
        // The original server's interpretation of value2 is not in the client.
        if (value1 != 0 || value2 != 1 || value3 != 0 || value4 != 0)
            return;

        npc.ClearAllAggro();
        npc.CurrentAggroTarget = null;
        npc.SetTarget(null);
        npc.IsInBattle = false;
        npc.Ai?.OnNoAggroTarget();
    }
}
