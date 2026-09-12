using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncOpenFarmInfo : DoodadFuncTemplate
{
    // doodad_funcs
    public uint FarmId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        // r208022 opens this UI locally from FarmId (native 393b8700). It reads
        // compact metadata; SCShowCommonFarmPacket belongs to the map marker cache.
        // The authored next_phase=-1 is not a request to delete the notice board.
        owner.ToNextPhase = false;
    }
}
