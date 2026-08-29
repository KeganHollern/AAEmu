using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncRatioRespawn : DoodadPhaseFuncTemplate
{
    public int Ratio { get; set; }
    public uint SpawnDoodadId { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        Logger.Trace("DoodadFuncRatioRespawn : Ratio {0}, SpawnDoodadId {1}", Ratio, SpawnDoodadId);

        // Replace the marker with the selected doodad through its authored spawner.
        if (owner.PhaseRatio <= Ratio && (owner.Spawner?.Id ?? 0) > 0)
        {
            var spawner = owner.Spawner;
            if (!DoodadManager.Instance.Exist(SpawnDoodadId))
            {
                Logger.Error(
                    $"DoodadFuncRatioRespawn: Spawn template {SpawnDoodadId} does not exist (spawner={spawner.Id}, currentTemplate={owner.TemplateId}).");
                owner.CumulativePhaseRatio -= Ratio;
                return false;
            }

            spawner.RespawnDoodadTemplateId = SpawnDoodadId;
            spawner.Despawn(owner);
            var spawned = spawner.Spawn(0);
            if (spawned == null)
                Logger.Error($"DoodadFuncRatioRespawn: Spawn failed for template {SpawnDoodadId} at spawner {spawner.Id}.");

            return true; // Interrupt the phase functions because the source doodad no longer exists.
        }

        owner.CumulativePhaseRatio -= Ratio;
        return false;
    }
}
