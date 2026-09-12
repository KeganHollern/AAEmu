using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class DeclareDominion : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.DeclareDominion;

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
        // The r208022 claim target and ownership transfer contract are not defined.
        // Do not publish sample ownership or consume the equipped pack.
        if (caster is Character character)
            character.SendMessage("Dominion claims are not available. Territories are unclaimed.");
    }
}
