using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncSign : DoodadPhaseFuncTemplate
{
    public string Name { get; set; }
    public int PickNum { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        // r208022 reads and localizes Name from its compact data. The hover path selects
        // a sign arm by PickNum and emits DRAW_DOODAD_SIGN_TAG locally (native 393b4bf0).
        // A phase can have several names, so do not replace the whole doodad's name here.
        Logger.Trace("DoodadFuncSign");
        return false;
    }
}
