using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class Combo : SpecialEffectAction
{
    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int comboSkillId,
        int timeFromNow,
        int value3,
        int value4)
    {
        if (caster is Character character)
        {
            character.Skills.ComboState.Arm((uint)Math.Max(comboSkillId, 0), timeFromNow);
            Logger.Debug("Special effects: Combo armed skill {0} for {1}ms", comboSkillId, timeFromNow);
        }
    }
}
