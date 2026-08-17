using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncPulseTrigger : DoodadPhaseFuncTemplate
{
    public bool Flag { get; set; }
    public int NextPhase { get; set; }

    /// <summary>
    /// aaemu-cluster#92: intentionally inert. The old implementation auto-advanced the phase for any
    /// Character caster and kept its "fired" latch on the SHARED phase-func template object, so one
    /// player tripping it leaked the state into every other world instance. XL's logic-family
    /// semantics for pulse triggers are unknown; explicit world scripts (the dungeon-script engine)
    /// drive these cross-doodad transitions instead.
    /// </summary>
    public override bool Use(BaseUnit caster, Doodad owner)
    {
        Logger.Trace($"DoodadFuncPulseTrigger: Flag={Flag}, NextPhase={NextPhase} (inert; driven by world scripts)");
        return false;
    }
}
