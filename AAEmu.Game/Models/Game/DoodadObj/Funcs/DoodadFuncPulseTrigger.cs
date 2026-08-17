using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncPulseTrigger : DoodadPhaseFuncTemplate
{
    public bool Flag { get; set; }
    public int NextPhase { get; set; }

    /// <summary>
    /// aaemu-cluster#92: no longer auto-advances phases. The old implementation OverridePhase'd for
    /// any Character caster and kept its "fired" latch on the SHARED phase-func template object, so
    /// one player tripping it leaked state into every other world instance. XL's logic-family
    /// semantics for pulse triggers are unknown; explicit world scripts (the dungeon-script engine)
    /// drive these cross-doodad transitions instead.
    ///
    /// MUST keep returning true: upstream always aborted the phase-func chain here, and several
    /// doodads (e.g. 3790/6002 with DoodadFuncRatioChange, 6194 with DoodadFuncTod) rely on that to
    /// keep phase-jumping funcs behind the trigger dormant at init. Returning false let those run
    /// and recurse DoPhaseFuncs through phase cycles until a StackOverflow killed the server at
    /// world load (incident: first #40 deployment).
    /// </summary>
    public override bool Use(BaseUnit caster, Doodad owner)
    {
        Logger.Trace($"DoodadFuncPulseTrigger: Flag={Flag}, NextPhase={NextPhase} (no auto-advance; driven by world scripts)");
        return true;
    }
}
