using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class Dyeing : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.Dyeing;

    internal static bool IsDyeingSkill(Skill skill) => skill.Template?.Effects.Any(effect =>
        effect.Template is SpecialEffect { SpecialEffectTypeId: SpecialType.Dyeing }) == true;

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
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (caster is not Character owner)
                return;
            var batch = SkillLaborBatch.For(owner);
            if (batch == null)
            {
                skill.Cancelled = true;
                return;
            }
            if (casterObj is not SkillItem source || targetObj is not SkillCastItemTarget destination ||
                owner.Inventory.GetItemById(destination.Id) is not EquipItem equipment ||
                owner.Inventory.GetItemById(source.ItemId) is not { Template.CategoryId: 33 } dye ||
                equipment.OwnerId != owner.Id || dye.OwnerId != owner.Id ||
                dye.SlotType != SlotType.Inventory || dye.Count <= 0 || equipment.Count <= 0 ||
                source.ItemTemplateId != dye.TemplateId || dye.Template.UseSkillId != skill.Template.Id ||
                !ItemManager.Instance.IsDyeableItem(equipment.TemplateId) ||
                TradeReservation.GetReservedCount(dye) != 0)
            {
                batch.Fail();
                owner.SendErrorMessage(ErrorMessageType.InvalidTarget);
                return;
            }
            if (equipment.SlotType != SlotType.Inventory)
            {
                batch.Fail();
                owner.SendErrorMessage(ErrorMessageType.CannotDyeingWhenEquipped);
                return;
            }
            if (!batch.Inventory.TryChangeDye(owner.Inventory.Bag, equipment, dye.TemplateId))
            {
                batch.Fail();
                owner.SendErrorMessage(ErrorMessageType.InvalidTarget);
            }
            // Skill.Apply consumes the dye in this same batch after the effect.
        }
    }
}
