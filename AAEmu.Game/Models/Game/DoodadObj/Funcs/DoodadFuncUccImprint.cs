using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncUccImprint : DoodadFuncTemplate
{
    // The r208022 legacy imprint UI only logs OPEN_EMBLEM_IMPRINT_UI. It does not
    // create a StampMaker purchase. Keep this separate from the supported crest printer
    // and do not grant its interaction context. See Docs/customized/World-Items-142-651.md.
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncUccImprint");

    }
}
