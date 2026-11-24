using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncInsertCounter : DoodadFuncTemplate
{
    // doodad_funcs
    public int Count { get; set; }
    public uint ItemId { get; set; }
    public int ItemCount { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        owner.ToNextPhase = false;
        if (caster is not Character character)
            return;

        var batch = SkillLaborBatch.For(character);
        if (batch == null || batch.Skill.Template.Id != skillId || Count <= 0 || ItemCount <= 0 ||
            owner.Data < 0 || owner.Data >= Count || nextPhase <= 0)
        {
            batch?.Fail();
            character.SendErrorMessage(ErrorMessageType.NoInteractionAvailable);
            return;
        }

        var pack = character.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack);
        if (pack == null || pack.TemplateId != ItemId || !batch.Inventory.TryConsume(character.Equipment, pack, ItemCount))
        {
            batch.Fail();
            character.SendErrorMessage(ErrorMessageType.NotEnoughItem);
            return;
        }

        var contributions = owner.Data + 1;
        owner.ToNextPhase = contributions == Count;
        owner.Data = owner.ToNextPhase ? 0 : contributions;
        var update = new SCDoodadChangedPacket(owner.ObjId, owner.Data);
        batch.AfterCommit(() => owner.BroadcastPacket(update, false));
    }
}
