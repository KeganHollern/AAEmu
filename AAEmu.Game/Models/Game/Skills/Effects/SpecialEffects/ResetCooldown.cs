using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ResetCooldown : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.ResetCooldown;

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
        // TODO ...
        if (caster is Character) { Logger.Debug("Special effects: ResetCooldown skillId {0}, tagId {1}, gcd {2}, value4 {3}", value1, value2, value3, value4); }

        var skillId = (uint)value1;
        var tagId = (uint)value2;
        var gcd = value3 == 1;
        if (caster is not Unit unit)
            return;
        if (skillId != 0)
            unit.Cooldowns.RemoveCooldown(skillId);
        if (tagId != 0)
            unit.Cooldowns.RemoveTagCooldown(tagId);
        if (gcd)
        {
            lock (unit.GcdLock)
            {
                unit.GlobalCooldown = DateTime.MinValue;
                unit.GlobalCooldownDurationMilliseconds = 0;
            }
        }
        if (caster is Character character)
            character.SendPacket(new SCSkillCooldownResetPacket(character, skillId, tagId, gcd));
    }
}
