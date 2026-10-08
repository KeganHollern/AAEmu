using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncCraftStartCraft : DoodadPhaseFuncTemplate
{
    public uint DoodadFuncCraftStartId { get; set; }
    public uint CraftId { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        // No r208022 doodad_phase_funcs row references this legacy recipe table.
        // Active workbenches use CraftPack and CSExecuteCraft. Starting a recipe
        // here would bypass the selected recipe, count, and material checks.
        return false;
    }
}
