using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.World;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncWaterVolume : DoodadPhaseFuncTemplate
{
    public float LevelChange { get; set; }
    public float Duration { get; set; }

    /// <summary>Search radius (m) around the doodad for the pool it controls. aaemu-cluster#98.</summary>
    private const float AreaSearchDistanceMeters = 100f;

    /// <summary>Side (m) of the synthetic square pool created when no ingested area is nearby. aaemu-cluster#98.</summary>
    private const float SyntheticAreaSizeMeters = 70f;

    /// <summary>Starting depth (m) of a synthetic pool; the surface starts at the doodad's Z and the raise adds depth on top.</summary>
    private const float SyntheticAreaDepthMeters = 2f;

    /// <summary>Seconds between surface animation steps.</summary>
    private const float StepSeconds = 1f;

    /// <summary>Number of ~1s steps that spread the level change over <paramref name="durationSeconds"/> (always ≥1).</summary>
    internal static int GetAnimationStepCount(float durationSeconds)
    {
        if (!float.IsFinite(durationSeconds) || durationSeconds <= StepSeconds)
            return 1;
        return (int)MathF.Ceiling(durationSeconds / StepSeconds);
    }

    /// <summary>Per-step surface delta so that stepCount steps sum to levelChange.</summary>
    internal static float GetAnimationStepDelta(float levelChange, int stepCount)
    {
        return levelChange / Math.Max(1, stepCount);
    }

    /// <summary>
    /// aaemu-cluster#92 / #98 / #93: raises (or drains, for negative LevelChange) the server-side
    /// water nearest to the owning doodad over Duration seconds. The client renders its own water
    /// from the doodad model; the server only needs a matching volume so IsWater, swimming and
    /// fall-damage checks agree with what players see (Sharpwind Mines flooding pools).
    /// </summary>
    public override bool Use(BaseUnit caster, Doodad owner)
    {
        var world = owner?.ParentWorld;
        if (world?.Water == null)
        {
            Logger.Warn($"DoodadFuncWaterVolume: no parent world/water for doodad {owner?.TemplateId}, LevelChange={LevelChange}, Duration={Duration}");
            return false;
        }

        var pos = owner.Transform.World.Position;
        var area = world.Water.GetNearestArea(pos, AreaSearchDistanceMeters);
        if (area == null)
        {
            area = world.Water.AddSquareArea($"WaterVolume_{owner.TemplateId}_{owner.ObjId}", pos,
                SyntheticAreaSizeMeters, SyntheticAreaDepthMeters);
            Logger.Info($"DoodadFuncWaterVolume: created synthetic area '{area.Name}' at {pos} in {world}; raising by {LevelChange} over {Duration}s");
        }
        else
        {
            Logger.Info($"DoodadFuncWaterVolume: raising area '{area.Name}' (id {area.Id}) by {LevelChange} over {Duration}s in {world}");
        }

        var steps = GetAnimationStepCount(Duration);
        var stepDelta = GetAnimationStepDelta(LevelChange, steps);
        TaskManager.Instance.Schedule(new WaterSurfaceChangeTask(world, area.Id, stepDelta),
            TimeSpan.FromSeconds(StepSeconds), TimeSpan.FromSeconds(StepSeconds), steps);

        // Never interrupt the phase-func sequence; the water animation runs in the background.
        return false;
    }
}
