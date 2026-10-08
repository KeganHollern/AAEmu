using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncCofferPerm : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        // The client opens the permission dialog locally. Its Apply button sends
        // CSChangeDoodadData, which changes and saves Doodad.Data through DoodadManager.
        // The skill does not select a permission or advance the coffer's phase.
        owner.ToNextPhase = false;
    }
}
