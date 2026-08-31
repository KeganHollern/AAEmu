using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ExitArchemall : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.ExitArchemall;

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
        if (caster is Character) { Logger.Debug($"Special effects: ExitArchemall value1 {value1}, value2 {value2}, value3 {value3}, value4 {value4}"); }

        if (caster is Character character)
        {
            ExitInstance(character, skill, () => IndunManager.Instance.RequestLeaveInstance(character));
        }
    }

    internal static void ExitInstance(Character character, Skill skill, Action requestLeave)
    {
        character.BroadcastPacket(new SCSkillEndedPacket(skill.TlId), true);
        requestLeave();
    }
}
