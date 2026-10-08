using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncParentInfo : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncParentInfo");
        // r208022 resolves ParentObjId from the doodad create packet and opens its
        // parent's house-info UI locally. It requests tax data with CSRequestHouseTax.
        // This descriptor does not need another parent packet or a name change.
    }
}
