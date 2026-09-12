using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ItemCapScale : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.ItemCapScale;

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
        if (caster is not Character owner)
            return;
        var batch = SkillLaborBatch.For(owner);
        if (casterObj is not SkillItem || targetObj is not SkillCastItemTarget itemTarget ||
            owner.Inventory.GetItemById(itemTarget.Id) is not EquipItem equipItem ||
            TradeReservation.GetReservedCount(equipItem) != 0)
        {
            Fail();
            return;
        }
        var scale = ItemManager.Instance.GetItemCapScale(skill.Id);
        if (scale == null || scale.ScaleMin < 0 || scale.ScaleMax <= scale.ScaleMin || scale.ScaleMax > ushort.MaxValue)
        {
            Fail();
            return;
        }

        var oldPhysical = equipItem.TemperPhysical;
        var oldMagical = equipItem.TemperMagical;
        var oldDirty = equipItem.IsDirty;
        batch?.Enlist(null, () =>
        {
            equipItem.TemperPhysical = oldPhysical;
            equipItem.TemperMagical = oldMagical;
            equipItem.IsDirty = oldDirty;
        });
        var physical = (ushort)Random.Shared.Next(scale.ScaleMin, scale.ScaleMax);
        var magical = (ushort)Random.Shared.Next(scale.ScaleMin, scale.ScaleMax);
        equipItem.TemperPhysical = physical;
        equipItem.TemperMagical = magical;
        equipItem.IsDirty = true;
        void Notify()
        {
            owner.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.EnchantPhysical, [new ItemUpdate(equipItem)], []));
            owner.SendMessage(ChatType.System, $"Temper:\n |cFFFFFFFF{physical}%|r Physical\n|cFFFFFFFF{magical}%|r Magical");
        }
        if (batch != null)
            batch.AfterCommit(Notify);
        else
            Notify();

        void Fail()
        {
            skill.Cancelled = true;
            batch?.Fail();
            owner.SendErrorMessage(ErrorMessageType.InvalidTarget);
        }
    }
}
