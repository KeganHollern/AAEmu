using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncDigTerrain : DoodadFuncTemplate
{
    // doodad_funcs
    public int Radius { get; set; }
    public int Life { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        // r208022 ClientDoodad::Init reads this descriptor and creates the terrain effect locally.
        // This is not a gathering action. See Docs/customized/Profession-Doodads-644.md.
        Logger.Trace("DoodadFuncDigTerrain");
    }
}
