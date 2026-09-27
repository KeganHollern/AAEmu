using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class RepairSlaveEffect : EffectTemplate
{
    public int Health { get; set; }
    public int Mana { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        if (caster is not Character player)
            return;
        var batch = SkillLaborBatch.For(player);
        if (batch == null)
        {
            if (source?.Skill != null)
                source.Skill.Cancelled = true;
            return;
        }

        var repairKit = ZoneSkillRestrictions.GetSourceItem(player, casterObj);
        if (casterObj is not SkillItem itemSource || repairKit?.Template.UseSkillAsReagent != true ||
            !SkillItemSource.CanUse(repairKit, player.ObjId, itemSource, source?.Skill?.Template, targetObj, skillObject) ||
            targetObj is not SkillCastItemTarget itemTarget || itemTarget.ObjId != player.ObjId ||
            player.Inventory.GetItemById(itemTarget.Id) is not SummonSlave item ||
            !ReferenceEquals(item._holdingContainer, player.Inventory.Bag) || item.OwnerId != player.Id ||
            item.SlaveDbId == 0 || item.IsDestroyed == 0 || TradeReservation.GetReservedCount(item) != 0 ||
            item.Template is not SummonSlaveTemplate template || Health <= 0 || Mana < 0 ||
            !SlaveGameData.Instance.HasRepairEffectId(template.SlaveId, Id))
        {
            player.SendErrorMessage(ErrorMessageType.ItemFailedRepair);
            batch.Fail();
            return;
        }

        var oldDestroyed = item.IsDestroyed;
        var oldTime = item.RepairStartTime;
        var oldDirty = item.IsDirty;
        batch.Enlist(context =>
        {
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            // The item is the authority for ownership. Two repairable summon templates
            // are tradable, so a destroyed vehicle can now belong to another character.
            command.CommandText = "UPDATE slaves SET hp=@hp,mp=@mp,updated_at=@time,owner_id=@owner,summoner=@owner " +
                "WHERE id=@id AND item_id=@item AND template_id=@template AND owner_type=0";
            command.Parameters.AddWithValue("@hp", Health);
            command.Parameters.AddWithValue("@mp", Mana);
            command.Parameters.AddWithValue("@time", time);
            command.Parameters.AddWithValue("@owner", player.Id);
            command.Parameters.AddWithValue("@id", item.SlaveDbId);
            command.Parameters.AddWithValue("@item", item.Id);
            command.Parameters.AddWithValue("@template", template.SlaveId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The destroyed vehicle does not match its owned summon item.");
        }, () =>
        {
            item.RepairStartTime = oldTime;
            item.IsDestroyed = oldDestroyed;
            item.IsDirty = oldDirty;
        });
        item.IsDestroyed = 0;
        item.RepairStartTime = time;
        item.IsDirty = true;
        batch.AfterCommit(() => player.SendPacket(new SCItemTaskSuccessPacket(
            ItemTaskType.RepairSlaves, new ItemUpdate(item), [])));
    }
}
