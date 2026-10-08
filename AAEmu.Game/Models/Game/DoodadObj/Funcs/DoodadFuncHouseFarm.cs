using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncHouseFarm : DoodadPhaseFuncTemplate
{
    public uint ItemCategoryId { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        Logger.Trace("DoodadFuncHouseFarm");
        // This phase descriptor identifies a harvest item category. It is not a garden-info action.
        // SCHouseFarmPacket produces a chat notice, and its retail trigger is not yet confirmed.
        // Do not send an invented whole-house crop count on each tagged phase transition.
        return false;
    }
}
