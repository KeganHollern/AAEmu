using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class RechargeItemBuff : SpecialEffectAction
{
    internal const uint Cleaner = 28242;
    internal const uint WashSkill = 22560;

    // Both festival costumes explicitly describe a new 30-day effect after washing.
    internal static bool CanWash(Character owner, EquipItem costume, DateTime time) =>
        ReferenceEquals(costume._holdingContainer, owner.Inventory.Bag) && costume.OwnerId == owner.Id && costume.Count == 1 &&
        costume.Template is ArmorTemplate { BindType: ItemBindType.BindOnEquip, RechargeBuffId: 7511,
            ChargeLifetime: 43200, ChargeCount: 0 } armor &&
        costume.ChargeStartTime != DateTime.MinValue && costume.ChargeStartTime <= time.AddMinutes(-armor.ChargeLifetime);

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
        if (skill.ExpiredCostume == null || caster is not Character owner)
            return;
        var batch = SkillLaborBatch.For(owner);
        if (batch == null)
        {
            skill.Cancelled = true;
            return;
        }
        if (targetObj is not SkillCastItemTarget destination ||
            destination.Id != skill.ExpiredCostume.Id || !CanWash(owner, skill.ExpiredCostume, time) ||
            casterObj is not SkillItem source ||
            owner.Inventory.Bag.GetItemByItemId(source.ItemId) is not { TemplateId: Cleaner, Count: > 0 } cleaner ||
            cleaner.OwnerId != owner.Id || TradeReservation.GetReservedCount(cleaner) != 0 ||
            !batch.Inventory.TryRestartCharge(owner.Inventory.Bag, skill.ExpiredCostume, time))
        {
            batch.Fail();
            owner.SendErrorMessage(ErrorMessageType.InvalidTarget);
        }
    }
}
