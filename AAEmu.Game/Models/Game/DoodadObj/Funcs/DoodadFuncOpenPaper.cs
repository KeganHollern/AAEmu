using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncOpenPaper : DoodadFuncTemplate
{
    // doodad_funcs
    public uint BookPageId { get; set; }
    public uint BookId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        // r208022 displays this locally (native 393b8450) and sends CSChangeDoodadPhase
        // only for a positive next phase. The packet validates and applies that transition.
        // A server skill must not repeat it or treat next_phase=-1 as object removal.
        owner.ToNextPhase = false;
    }
}
