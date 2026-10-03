using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills;

internal static class MateRecovery
{
    internal const uint GetUpSkillId = 13719;

    internal static bool IsRecoverySkill(SkillTemplate template) => template?.Effects.Any(effect =>
        effect.Template is SpecialEffect
        { SpecialEffectTypeId: SpecialType.MateMakeGetUp or SpecialType.HealPet }) == true;

    internal static bool IsGetUpSkill(SkillTemplate template) => template?.Id == GetUpSkillId &&
        template.Effects.Any(effect => effect.Template is SpecialEffect
            { SpecialEffectTypeId: SpecialType.MateMakeGetUp });

    internal static BaseUnit RequirementTarget(SkillTemplate template, BaseUnit caster, BaseUnit target) =>
        IsGetUpSkill(template) ? caster : target;

    internal static Units.Mate GetOwnedTarget(Character owner, SkillCastTarget target)
    {
        if (owner == null || owner.IsDead || target is not SkillCastUnitTarget || owner.ParentWorld == null ||
            owner.ParentWorld.GetUnit(target.ObjId) is not Units.Mate mate || mate.IsTemporarySummon || mate.IsDead ||
            mate.AttachmentsRetired || !ReferenceEquals(owner.ParentWorld, mate.ParentWorld) ||
            owner.Transform.InstanceId != mate.Transform.InstanceId ||
            !owner.ParentWorld.MateManager.IsOwnedMate(owner, mate) ||
            mate.SummonItem is not { } item || item.OwnerId != owner.Id ||
            !ReferenceEquals(owner.Inventory.GetItemById(item.Id), item) ||
            !ReferenceEquals(item._holdingContainer, owner.Inventory.Bag) ||
            TradeReservation.GetReservedCount(item) != 0 ||
            mate.DbInfo == null || !ReferenceEquals(owner.Mates.GetMateInfo(item.Id), mate.DbInfo))
            return null;
        return mate;
    }

    internal static bool CanUseGetUp(Character owner, SkillTemplate template, SkillCaster source,
        SkillCastTarget target)
    {
        return owner != null && IsGetUpSkill(template) && source is SkillCasterUnit && source.ObjId == owner.ObjId &&
            GetOwnedTarget(owner, target) is { IsDowned: true };
    }

    internal static SkillResult Check(BaseUnit caster, SkillCaster source, SkillCastTarget target, Skill skill)
    {
        if (!IsRecoverySkill(skill.Template))
            return SkillResult.Success;
        if (caster is not Character owner || GetOwnedTarget(owner, target) is not { } mate)
            return SkillResult.InvalidTarget;
        if (skill.InitialTarget != null && !ReferenceEquals(skill.InitialTarget, mate))
            return SkillResult.InvalidTarget;
        if (IsGetUpSkill(skill.Template))
        {
            if (!CanUseGetUp(owner, skill.Template, source, target))
                return SkillResult.InvalidTarget;
        }
        else
        {
            var item = ZoneSkillRestrictions.GetSourceItem(owner, source);
            if (source is not SkillItem itemSource || item?.Template.UseSkillAsReagent != true ||
                !SkillItemSource.CanUse(item, owner.ObjId, itemSource, skill.Template, target, null))
                return SkillResult.InvalidSource;
            if (!mate.IsInjured && mate.Hp >= mate.MaxHp && mate.Mp >= mate.MaxMp)
                return SkillResult.InvalidTarget;
        }
        return SkillRange.Check(skill, owner, mate);
    }

    internal static bool Validate(BaseUnit caster, SkillCaster source, SkillCastTarget target, Skill skill)
    {
        if (!IsRecoverySkill(skill.Template))
            return true;
        if (Check(caster, source, target, skill) == SkillResult.Success &&
            SkillRequirementsGameData.Instance.GetFailedRequirement(skill.Template, caster,
                RequirementTarget(skill.Template, caster, GetOwnedTarget(caster as Character, target))) == 0)
            return true;
        skill.Cancelled = true;
        if (caster is Character owner)
        {
            owner.SendErrorMessage(ErrorMessageType.InvalidTarget);
            SkillLaborBatch.For(owner)?.Fail();
        }
        return false;
    }

    internal static void GetUp(BaseUnit caster, SkillCaster source, SkillCastTarget target, Skill skill)
    {
        if (skill == null || caster is not Character owner)
            return;
        var batch = SkillLaborBatch.For(owner);
        if (batch == null || !Validate(caster, source, target, skill))
        {
            skill.Cancelled = true;
            return;
        }
        var mate = GetOwnedTarget(owner, target);
        mate.StageRecovery(batch, true, mate.Hp, mate.Mp);
    }

    internal static void Heal(BaseUnit caster, SkillCaster source, SkillCastTarget target, Skill skill,
        int healthPercent, int manaPercent)
    {
        if (skill == null || caster is not Character owner)
            return;
        var batch = SkillLaborBatch.For(owner);
        if (batch == null || !Validate(caster, source, target, skill) ||
            healthPercent is <= 0 or > 100 || manaPercent is < 0 or > 100)
        {
            skill.Cancelled = true;
            batch?.Fail();
            return;
        }
        var mate = GetOwnedTarget(owner, target);
        var health = RestorePoints(mate.Hp, mate.MaxHp, healthPercent);
        var mana = RestorePoints(mate.Mp, mate.MaxMp, manaPercent);
        mate.StageRecovery(batch, false, health, mana);
    }

    internal static int RestorePoints(int current, int maximum, int percent) =>
        (int)Math.Clamp((long)current + (long)maximum * percent / 100, 0, maximum);
}
