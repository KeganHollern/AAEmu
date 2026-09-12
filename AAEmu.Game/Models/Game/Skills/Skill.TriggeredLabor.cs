using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Skills;

public partial class Skill
{
    private SkillLaborBatch _triggeredLaborBatch;

    internal static bool CanSettleTriggeredEffect(SpecialEffect effect) =>
        CanSettleTriggeredEffect(effect, []);

    private static bool CanSettleTriggeredEffect(SpecialEffect effect, HashSet<uint> visiting)
    {
        if (effect.Value1 <= 0 || effect.Value2 < 0)
            return false;
        var template = SkillManager.Instance.GetSkillTemplate((uint)effect.Value1);
        if (template == null || !visiting.Add(template.Id))
            return false;
        try
        {
            if (template.Plot != null || template.ChannelingTime > 0 || template.SkillControllerId != 0 ||
                template.ToggleBuffId != 0 || template.ChannelingBuffId != 0 || template.ChannelingTargetBuffId != 0 ||
                template.Unmount || template.EffectRepeatCount > 1 ||
                SkillManager.Instance.GetSkillReagentsBySkillId(template.Id).Count > 0 ||
                SkillManager.Instance.GetSkillProductsBySkillId(template.Id).Count > 0 ||
                template.Effects.Any(value => value.ConsumeSourceItem || value.ConsumeItemId != 0 || value.ItemSetId != 0))
                return false;
            if (IsTriggeredPresentation(template))
                return true;
            if (effect.Value2 != 0 || template.CastingTime != 0 || template.CooldownTime != 0 ||
                template.EffectDelay != 0 || template.EffectSpeed != 0 ||
                (template.UseAnimTime && template.FireAnim?.CombatSyncTime > 0))
                return false;
            return template.Effects.All(value => value.Template switch
            {
                BuffEffect or GainLootPackItemEffect => true,
                SpecialEffect { SpecialEffectTypeId: SpecialType.SkillUse } nested => CanSettleTriggeredEffect(nested, visiting),
                _ => false
            });
        }
        finally { visiting.Remove(template.Id); }
    }

    internal static bool IsTriggeredPresentation(SkillTemplate template) =>
        template.ConsumeLaborPower == 0 && template.GainLifePoint == 0 &&
        template.Effects.All(value => value.Template is BubbleEffect);

    internal bool ApplyTriggeredLaborEffects(SkillLaborBatch batch, BaseUnit caster, SkillCaster casterCaster,
        BaseUnit requestedTarget, SkillObject skillObject)
    {
        if (!ReferenceEquals(SkillLaborBatch.Current, batch) || caster is not Unit unit ||
            (caster is Character character && !ReferenceEquals(character, batch.Owner)))
            return batch.Fail();
        var targetCaster = new SkillCastUnitTarget(requestedTarget?.ObjId ?? 0);
        OriginalCaster = caster;
        SourceItemTemplateId = batch.Skill.SourceItemTemplateId;
        var target = GetInitialTarget(caster, casterCaster, targetCaster);
        if (target == null || UnitRequirementsGameData.Instance.CanUseSkill(Template, caster, casterCaster).ResultKey != SkillResultKeys.ok ||
            SkillRequirementsGameData.Instance.GetFailedRequirement(Template, caster, target) != 0 ||
            !ZoneSkillRestrictions.CanApply(caster, this, casterCaster))
            return batch.Fail();
        var range = caster.ApplySkillModifiers(this, Static.SkillAttribute.Range, Template.MaxRange);
        var distance = unit.GetDistanceTo(target, true);
        if (distance < Template.MinRange || distance > range)
            return batch.Fail();
        var mana = ManaCost(unit);
        if (unit.Mp < mana || (caster is Character && !batch.TryConsumeChildLabor(this)))
            return batch.Fail();
        if (mana > 0 && unit.Hp > 0)
        {
            var oldMana = unit.Mp;
            unit.Mp -= mana;
            batch.Enlist(null, () => unit.Mp = oldMana);
            batch.AfterCommit(() => unit.BroadcastPacket(new SCUnitPointsPacket(unit.ObjId, unit.Hp, unit.Mp), true));
        }
        InitialTarget = target;
        _triggeredLaborBatch = batch;
        skillObject ??= new SkillObject();
        TlId = SkillTlIdManager.GetNextId(caster);
        var childTlId = TlId;
        batch.Enlist(null, () => SkillTlIdManager.ReleaseId(childTlId));
        batch.AfterCommit(() =>
        {
            caster.Buffs.TriggerRemoveOn(BuffRemoveOn.UseSkill);
            caster.BroadcastPacket(new SCSkillStartedPacket(Id, childTlId, casterCaster, targetCaster, this, skillObject), true);
            caster.BroadcastPacket(new SCSkillFiredPacket(Id, childTlId, casterCaster, targetCaster, this, skillObject), true);
        });
        try
        {
            ApplyEffects(caster, casterCaster, target, targetCaster, skillObject);
            if (Cancelled || batch.Owner.SkillCancelled)
                return batch.Fail();
            if (caster is Character owner && Template.GainLifePoint > 0)
            {
                owner.ChangeGamePoints(GamePointKind.Vocation,
                    (int)Math.Ceiling(AppConfiguration.Instance.World.VocationRate * Template.GainLifePoint));
                LaborVocationSettled = true;
            }
            batch.AfterCommit(() =>
            {
                if (caster.GetOwnerCharacter()?.Achievements != null)
                    RecordUseSkillAchievement(caster);
                caster.BroadcastPacket(new SCSkillEndedPacket(childTlId), true);
                SkillTlIdManager.ReleaseId(childTlId);
                TlId = 0;
            });
            return true;
        }
        finally { _triggeredLaborBatch = null; }
    }
}
