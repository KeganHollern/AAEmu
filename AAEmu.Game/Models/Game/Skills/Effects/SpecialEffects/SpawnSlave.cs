using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class SpawnSlave : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.SpawnSlave;

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
        if (caster is Character owner && casterObj is SkillItem skillData &&
            SlavePlacementRequest.TryReadTarget(targetObj, out var request) &&
            owner.ParentWorld.SlaveManager.Create(owner, skillData, request))
            return;

        skill.Cancelled = true;
        SkillLaborBatch.Current?.Fail();
    }
}
