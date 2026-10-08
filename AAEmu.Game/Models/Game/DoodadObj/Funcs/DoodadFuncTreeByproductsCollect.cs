using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncTreeByproductsCollect : DoodadFuncTemplate
{
    // Keep the exact compact type name for reflection-based lookup. The r208022 rows
    // only reference a removed phase group and an unspawned test doodad; they define no
    // live collection operation. See Docs/customized/World-Items-142-651.md.
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncTreeByproductsCollect");

    }
}
