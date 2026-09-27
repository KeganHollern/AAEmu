using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Items.Procs;

/// <summary>A proc activation and its cooldown. Equipment refreshes retain this state.</summary>
public class ItemProc
{
    private int _applying;

    public uint TemplateId => Template.Id;
    public ItemProcTemplate Template { get; }
    public DateTime LastProc { get; private set; } = DateTime.MinValue;

    public ItemProc(uint templateId) : this(ItemManager.Instance.GetItemProcTemplate(templateId))
    {
    }

    internal ItemProc(ItemProcTemplate template)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
    }

    internal bool Apply(Unit owner, Unit eventTarget, bool killingBlow, double chance, int itemLevel, DateTime now,
        Func<double> roll, Func<Unit, Unit, SkillTemplate, int, bool> cast, bool ignoreRoll = false)
    {
        if (Interlocked.Exchange(ref _applying, 1) != 0)
            return false;
        try
        {
            if (owner.Hp <= 0 || Template.SkillTemplate == null || Template.Finisher && !killingBlow ||
                now < LastProc.AddSeconds(Template.CooldownSec))
                return false;
            if (!ignoreRoll && (chance <= 0 || roll() * 100 >= Math.Clamp(chance, 0, 100)))
                return false;
            var target = GetTarget(owner, eventTarget, Template.SkillTemplate);
            if (target == null || !cast(owner, target, Template.SkillTemplate, itemLevel))
                return false;
            LastProc = now;
            return true;
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }

    internal static Unit GetTarget(Unit owner, Unit eventTarget, SkillTemplate skill)
    {
        return skill.TargetType == SkillTargetType.Self ? owner : eventTarget;
    }

    internal static bool Cast(Unit owner, Unit target, SkillTemplate template, int itemLevel)
    {
        var skill = new Skill(template) { IsItemProc = true, Level = (byte)Math.Clamp(itemLevel, 1, byte.MaxValue) };
        return skill.Use(owner, new SkillCasterUnit(owner.ObjId), new SkillCastUnitTarget(target.ObjId),
            null, true, out _) == SkillResult.Success;
    }
}
