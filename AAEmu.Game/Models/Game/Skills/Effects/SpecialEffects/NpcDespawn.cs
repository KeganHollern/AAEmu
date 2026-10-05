using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class NpcDespawn : SpecialEffectAction
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
        if (target is not Npc npc || npc.ParentWorld?.SpawnManager == null)
            return;

        if (npc.Spawner != null)
        {
            npc.Spawner.DespawnFromEffect(npc);
            return;
        }

        lock (npc.AggroTable)
        {
            if (npc.Despawned || npc.CombatRetired)
                return;
            npc.Despawned = true;
        }
        npc.ParentWorld.SpawnManager.DespawnObject(npc);
    }
}
