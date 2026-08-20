﻿using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

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

    /// <summary>
    /// aaemu-cluster#92 / #98 / #93: raises (or drains, for negative LevelChange) the server-side
    /// water nearest to the owning doodad over Duration seconds. The client renders its own water
    /// from the doodad model and lerps it continuously; WaterBodies.AnimateAreaSurface interpolates
    /// the server surface the same way, so IsWater, swimming and fall-damage checks agree with what
    /// players see at every moment of the rise (a stepped server surface flipped the underwater
    /// state per step and bounced swimming players — Sharpwind Mines flooding pools).
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
            // Corner-anchored, not centered: the client's water prefabs (e.g. Sharpwind's
            // cuttingwind.water1, a 65x69 quad) keep their origin at a corner and extend +X/+Y,
            // so a doodad authored at that anchor must produce server water with the same
            // footprint or swim/breath checks disagree with the visual (aaemu-cluster#92
            // validation: the pit water rendered offset into one quadrant).
            var center = pos + new System.Numerics.Vector3(SyntheticAreaSizeMeters / 2f, SyntheticAreaSizeMeters / 2f, 0f);
            area = world.Water.AddSquareArea($"WaterVolume_{owner.TemplateId}_{owner.ObjId}", center,
                SyntheticAreaSizeMeters, SyntheticAreaDepthMeters);
            Logger.Info($"DoodadFuncWaterVolume: created synthetic area '{area.Name}' anchored at {pos} in {world}; raising by {LevelChange} over {Duration}s");
        }
        else
        {
            Logger.Info($"DoodadFuncWaterVolume: raising area '{area.Name}' (id {area.Id}) by {LevelChange} over {Duration}s in {world}");
        }

        world.Water.AnimateAreaSurface(area.Id, LevelChange, Duration);

        // Never interrupt the phase-func sequence; the water animation runs in the background.
        return false;
    }
}
