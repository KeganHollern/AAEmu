using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    internal void RefreshPatronBuff()
    {
        const uint patronBuffId = 8000011;
        if (Connection?.Payment.PremiumState != true)
        {
            if (Buffs.CheckBuff(patronBuffId))
                Buffs.RemoveBuff(patronBuffId);
            return;
        }
        if (!Buffs.CheckBuff(patronBuffId) && SkillManager.Instance.GetBuffTemplate(patronBuffId) is { } template)
            Buffs.AddBuff(new Buff(this, this, new SkillCasterUnit(ObjId), template, null, DateTime.UtcNow));
    }
}
