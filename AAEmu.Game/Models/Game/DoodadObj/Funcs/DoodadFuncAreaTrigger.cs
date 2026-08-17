using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncAreaTrigger : DoodadFuncTemplate
{
    // doodad_funcs
    public uint NpcId { get; set; }
    public bool IsEnter { get; set; }

    /// <summary>
    /// aaemu-cluster#92 / #95: proximity sensing is armed per-instance by DoodadAreaTriggerRegistry
    /// when a phase containing this func is entered (see Doodad.DoChangePhase); a player directly
    /// using the doodad must NOT advance the phase, so this stays a no-op.
    /// </summary>
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace($"DoodadFuncAreaTrigger: NpcId={NpcId}, IsEnter={IsEnter} (armed by DoodadAreaTriggerRegistry, not by Use)");
    }
}
