using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncStampMaker : DoodadFuncTemplate
{
    // doodad_funcs
    public int ConsumeMoney { get; set; }
    public uint ItemId { get; set; }
    public uint ConsumeItemId { get; set; }
    public int ConsumeCount { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is Character character && ServiceInteraction.CanReach(character, owner) &&
            owner.CurrentFuncs.Any(func => func.FuncType == nameof(DoodadFuncStampMaker) && func.FuncId == Id))
            character.CurrentInteractionObject = owner;
    }
}
