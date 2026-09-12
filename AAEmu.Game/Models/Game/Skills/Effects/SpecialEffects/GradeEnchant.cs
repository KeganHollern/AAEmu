using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class GradeEnchant : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.GradeEnchant;

    private enum GradeEnchantResult
    {
        Break = 0,
        Downgrade = 1,
        Fail = 2,
        Success = 3,
        GreatSuccess = 4
    }

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
            ExecuteLocked(caster, casterObj, targetObj, skill, skillObject, value1, value3);
    }

    private static void ExecuteLocked(BaseUnit caster, SkillCaster casterObj, SkillCastTarget targetObj,
        Skill skill, SkillObject skillObject, int value1, int value3)
    {

        if (caster is not Character character)
            return;
        var batch = SkillLaborBatch.For(character);
        if (batch == null)
            return;
        if (casterObj is not SkillItem scroll || targetObj is not SkillCastItemTarget itemTarget)
        {
            batch.Fail();
            return;
        }

        var item = character.Inventory.GetItemById(itemTarget.Id);
        var scrollItem = character.Inventory.GetItemById(scroll.ItemId);
        if (item == null || item.SlotType is not (SlotType.Inventory or SlotType.Equipment) ||
            scrollItem == null || scrollItem.SlotType != SlotType.Inventory ||
            TradeReservation.GetReservedCount(item) != 0 || TradeReservation.GetReservedCount(scrollItem) != 0)
        {
            batch.Fail();
            return;
        }
        var initialGrade = item.Grade;
        var gradeTemplate = ItemManager.Instance.GetGradeTemplate(initialGrade);
        var cost = gradeTemplate == null ? -1 : GoldCost(gradeTemplate, item, value3);
        if (cost < 0)
        {
            batch.Fail();
            return;
        }

        ItemGradeEnchantingSupport charmInfo = null;
        Item charmItem = null;
        if (skillObject is SkillObjectItemGradeEnchantingSupport { SupportItemId: not 0 } charm)
        {
            charmItem = character.Inventory.GetItemById(charm.SupportItemId);
            charmInfo = charmItem == null ? null : ItemManager.Instance.GetItemGradEnchantingSupportByItemId(charmItem.TemplateId);
            if (charmInfo == null || charmItem.SlotType != SlotType.Inventory ||
                TradeReservation.GetReservedCount(charmItem) != 0 ||
                (charmInfo.RequireGradeMin != -1 && initialGrade < charmInfo.RequireGradeMin) ||
                (charmInfo.RequireGradeMax != -1 && initialGrade > charmInfo.RequireGradeMax))
            {
                batch.Fail();
                character.SendErrorMessage(ErrorMessageType.NotEnoughRequiredItem);
                return;
            }
        }
        if (!batch.Inventory.TryChangeMoney(character, -cost))
        {
            batch.Fail();
            character.SendErrorMessage(ErrorMessageType.NotEnoughMoney);
            return;
        }
        if (charmItem != null && !batch.Inventory.TryConsume(character.Inventory.Bag, charmItem, 1))
        {
            batch.Fail();
            return;
        }

        var (result, grade) = RollRegrade(gradeTemplate, initialGrade, value1 != 0, charmInfo != null, charmInfo);
        var changed = result == GradeEnchantResult.Break
            ? batch.Inventory.TryConsume(item._holdingContainer, item, item.Count)
            : batch.Inventory.TryChangeGrade(item._holdingContainer, item, grade);
        if (!changed)
        {
            batch.Fail();
            return;
        }

        // Skill.Apply consumes the authored scroll in the same batch. Publish
        // the roll only after labor, gold, charm, scroll and item state commit.
        batch.AfterCommit(() =>
        {
            if (result is GradeEnchantResult.Success or GradeEnchantResult.GreatSuccess)
                character.Achievements?.Increment(CharRecordKind.EnchantItem, grade, 0);
            else
                character.Achievements?.Increment(CharRecordKind.EnchantFailure, 0, 0);
            character.SendPacket(new SCGradeEnchantResultPacket((byte)result, item, initialGrade, grade));
            character.BroadcastPacket(new SCSkillEndedPacket(skill.TlId), true);
            if (grade >= 8 && result is GradeEnchantResult.Success or GradeEnchantResult.GreatSuccess)
                WorldManager.Instance.BroadcastPacketToServer(
                    new SCGradeEnchantBroadcastPacket(character.Name, (byte)result, item, initialGrade, grade));
        });
    }

    private static (GradeEnchantResult Result, byte Grade) RollRegrade(GradeTemplate gradeTemplate, byte initialGrade, bool isLucky, bool useCharm,
        ItemGradeEnchantingSupport charmInfo)
    {
        var successRoll = Random.Shared.Next(0, 10000);
        var breakRoll = Random.Shared.Next(0, 10000);
        var downgradeRoll = Random.Shared.Next(0, 10000);
        var greatSuccessRoll = Random.Shared.Next(0, 10000);

        // TODO : Refactor
        var successChance = useCharm
            ? GetCharmChance(gradeTemplate.EnchantSuccessRatio, charmInfo.AddSuccessRatio, charmInfo.AddSuccessMul)
            : gradeTemplate.EnchantSuccessRatio;
        var greatSuccessChance = useCharm
            ? GetCharmChance(gradeTemplate.EnchantGreatSuccessRatio, charmInfo.AddGreatSuccessRatio,
                charmInfo.AddGreatSuccessMul)
            : gradeTemplate.EnchantGreatSuccessRatio;
        var breakChance = useCharm
            ? GetCharmChance(gradeTemplate.EnchantBreakRatio, charmInfo.AddBreakRatio, charmInfo.AddBreakMul)
            : gradeTemplate.EnchantBreakRatio;
        var downgradeChance = useCharm
            ? GetCharmChance(gradeTemplate.EnchantDowngradeRatio, charmInfo.AddDowngradeRatio,
                charmInfo.AddDowngradeMul)
            : gradeTemplate.EnchantDowngradeRatio;

        if (successRoll < successChance)
        {
            if (isLucky && greatSuccessRoll < greatSuccessChance)
            {
                // TODO : Refactor
                var increase = useCharm ? 2 + charmInfo.AddGreatSuccessGrade : 2;
                var next = GetNextGrade(gradeTemplate, increase);
                return next == null ? (GradeEnchantResult.Fail, initialGrade) : (GradeEnchantResult.GreatSuccess, (byte)next.Grade);
            }

            var nextGrade = GetNextGrade(gradeTemplate, 1);
            return nextGrade == null ? (GradeEnchantResult.Fail, initialGrade) : (GradeEnchantResult.Success, (byte)nextGrade.Grade);
        }

        if (breakRoll < breakChance)
        {
            return (GradeEnchantResult.Break, initialGrade);
        }

        if (downgradeRoll < downgradeChance)
        {
            if (gradeTemplate.EnchantDowngradeMin < 0 || gradeTemplate.EnchantDowngradeMax <= gradeTemplate.EnchantDowngradeMin)
                return (GradeEnchantResult.Fail, initialGrade);
            var newGrade = (byte)Random.Shared.Next(gradeTemplate.EnchantDowngradeMin, gradeTemplate.EnchantDowngradeMax);
            return (GradeEnchantResult.Downgrade, newGrade);
        }

        return (GradeEnchantResult.Fail, initialGrade);
    }

    private static int GoldCost(GradeTemplate gradeTemplate, Item item, int ItemType)
    {
        uint slotTypeId = (ItemType, item.Template) switch
        {
            (1, WeaponTemplate weapon) => weapon.HoldableTemplate?.SlotTypeId ?? 0,
            (2, ArmorTemplate armor) => armor.SlotTemplate?.SlotTypeId ?? 0,
            (24, AccessoryTemplate accessory) => accessory.SlotTemplate?.SlotTypeId ?? 0,
            _ => 0
        };
        if (slotTypeId == 0)
            return -1;
        var enchantingCost = ItemManager.Instance.GetEquipSlotEnchantingCost(slotTypeId);
        if (enchantingCost == null)
            return -1;

        var itemGrade = gradeTemplate.EnchantCost;
        var itemLevel = item.Template.Level;
        var equipSlotEnchantCost = enchantingCost.Cost;

        var parameters = new Dictionary<string, double>
        {
            { "item_grade", itemGrade },
            { "item_level", itemLevel },
            { "equip_slot_enchant_cost", equipSlotEnchantCost }
        };
        var formula = FormulaManager.Instance.GetFormula((uint)FormulaKind.GradeEnchantCost);

        if (formula == null)
            return -1;
        var cost = formula.Evaluate(parameters);
        return double.IsFinite(cost) && cost >= 0 && cost <= int.MaxValue ? (int)cost : -1;
    }

    private static GradeTemplate GetNextGrade(GradeTemplate currentGrade, int gradeChange)
    {
        return ItemManager.Instance.GetGradeTemplateByOrder(currentGrade.GradeOrder + gradeChange);
    }

    private static int GetCharmChance(int baseChance, int charmRatio, int charmMul)
    {
        return baseChance + charmRatio + (int)(baseChance * (charmMul / 100.0));
    }
}
