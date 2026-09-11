using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ItemSocketing : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.ItemSocketing;
    protected virtual int RollSocketChance() => Random.Shared.Next(0, 10000);

    internal static bool IsSocketingSkill(Skill skill) => skill.Template?.Effects.Any(effect =>
        effect.Template is SpecialEffect { SpecialEffectTypeId: SpecialType.ItemSocketing }) == true;

    internal static bool ValidateSkill(BaseUnit caster, SkillCaster casterObj, SkillCastTarget targetObj, Skill skill)
    {
        if (!IsSocketingSkill(skill))
            return true;
        lock (SaveManager.PersistenceSyncRoot)
        {
            var error = Validate(caster, casterObj, targetObj, skill, out _, out _, out _);
            if (error == ErrorMessageType.NoErrorMessage)
                return true;
            Reject(caster, skill, error);
            return false;
        }
    }

    private static ErrorMessageType Validate(BaseUnit caster, SkillCaster casterObj, SkillCastTarget targetObj,
        Skill skill, out EquipItem equipment, out Item gem, out int gemCount)
    {
        equipment = null;
        gem = null;
        gemCount = 0;
        if (caster is not Character owner || casterObj is not SkillItem source || targetObj is not SkillCastItemTarget target)
            return ErrorMessageType.InvalidTarget;
        equipment = owner.Inventory.GetItemById(target.Id) as EquipItem;
        gem = owner.Inventory.GetItemById(source.ItemId);
        if (equipment == null || gem?.Template == null || gem.Count <= 0 || equipment.Count <= 0 ||
            gem.OwnerId != owner.Id || equipment.OwnerId != owner.Id ||
            gem._holdingContainer?.OwnerId != owner.Id || equipment._holdingContainer?.OwnerId != owner.Id ||
            source.ItemTemplateId != gem.TemplateId || gem.Template.UseSkillId != skill.Template.Id)
            return ErrorMessageType.InvalidTarget;
        return ItemManager.Instance.SocketingRules.Validate(equipment, gem.TemplateId, out gemCount);
    }

    private static void Reject(BaseUnit caster, Skill skill, ErrorMessageType error)
    {
        skill.Cancelled = true;
        if (caster is Character owner)
        {
            owner.SkillCancelled = true;
            owner.ConditionChance = false;
            owner.SendPacket(new SCErrorMsgPacket(error, 0, true));
        }
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
            ExecuteLocked(caster, casterObj, targetObj, skill, value1, value2, value3, value4);
    }

    private void ExecuteLocked(BaseUnit caster, SkillCaster casterObj, SkillCastTarget targetObj,
        Skill skill, int value1, int value2, int value3, int value4)
    {
        Logger.Debug("Special effects: ItemSocketing value1 {0}, value2 {1}, value3 {2}, value4 {3}", value1, value2, value3, value4);
        if (skill.Cancelled)
            return;
        var error = Validate(caster, casterObj, targetObj, skill, out var equipItem, out var gemItem, out var gemCount);
        if (error != ErrorMessageType.NoErrorMessage)
        {
            Reject(caster, skill, error);
            return;
        }
        var owner = (Character)caster;

        var tasksSocketing = new List<ItemTask>();

        byte result = 0;
        var installed = false;
        if (gemItem.TemplateId != Item.DawnStone)
        {
            // Roll for Success
            var gemRoll = RollSocketChance();
            var gemChance = ItemManager.Instance.GetSocketChance((uint)gemCount); // fetches chances from sqlite3
            // var gemChance = int.MaxValue; //gives 100% success rates

            if (gemRoll < gemChance)
            {
                // Success
                equipItem.GemIds[Array.IndexOf(equipItem.GemIds, 0u)] = gemItem.TemplateId;
                result = 1;
            }
            else
            {
                // Failed: on retail for this client version (r208022) a failed socket
                // attempt removes ALL currently socketed gems. This harsh behavior is
                // what made early gear progression so punishing. (Guild gems, which
                // cannot fail, only got the keep-on-fail behavior in later versions.)
                for (var i = 0; i < equipItem.GemIds.Length; i++)
                {
                    equipItem.GemIds[i] = 0;
                }
            }
            installed = true;
        }
        else
        {
            // DawnStone
            for (var i = 0; i < equipItem.GemIds.Length; i++)
            {
                equipItem.GemIds[i] = 0;
            }
            result = 1;
        }

        equipItem.IsDirty = true;
        tasksSocketing.Add(new ItemUpdate(equipItem));

        owner.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.Socketing, tasksSocketing, []));
        owner.SendPacket(new SCItemSocketingLunagemResultPacket(result, equipItem.Id, gemItem.TemplateId, installed));
        owner.BroadcastPacket(new SCSkillEndedPacket(skill.TlId), true);
    }
}
