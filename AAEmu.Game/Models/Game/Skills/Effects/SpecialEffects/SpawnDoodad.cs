using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class SpawnDoodad : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.SpawnDoodad;

    /// <summary>
    /// Spawns a doodad
    /// </summary>
    /// <param name="caster">Original caster</param>
    /// <param name="casterObj">Skill caster object</param>
    /// <param name="target">Skill cast target BaseUnit</param>
    /// <param name="targetObj">Skill cast target object</param>
    /// <param name="castObj">Cast action</param>
    /// <param name="skill">Original skill (if any)</param>
    /// <param name="skillObject">Skill object</param>
    /// <param name="time">Start time</param>
    /// <param name="doodadId">Doodad templateId</param>
    /// <param name="delay">Delay before the doodad becomes "active"?</param>
    /// <param name="createTradePack">Set to one for trade packs created for quests</param>
    /// <param name="value4">Unused</param>
    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int doodadId,
        int delay,
        int createTradePack,
        int value4
    )
    {
        if (caster is null)
        {
            Logger.Warn($"Special effects: SpawnDoodad has no caster defined, doodadId {doodadId}, delay {delay}, createTradePack {createTradePack}, value4 {value4}");
            return;
        }

        if (caster is Character)
        {
            Logger.Debug($"Special effects: SpawnDoodad doodadId {doodadId}, delay {delay}, createTradePack {createTradePack}, value4 {value4}");
        }

        var rpy = target.Transform.World.ToRollPitchYawDegrees();
        var placementSource = (skill?.Template.TargetSelection ?? 0) switch
        {
            SkillTargetSelection.Target => target,
            _ => caster
        };
        var placementPolicy = placementSource.Transform.Parent is not null ||
                              placementSource.Transform.StickyParent is not null
            ? DynamicDoodadPlacementPolicy.PreserveParentedHeight
            : DynamicDoodadPlacementPolicy.GroundToNearbySurface;
        if (!TryResolvePlacement(caster.ParentWorld.Template.GeoData, placementSource.Transform.World.Position,
                rpy.Z, placementPolicy, out var placementPosition))
        {
            Logger.Warn($"Special effects: SpawnDoodad cannot place doodadId {doodadId} at {placementSource.Transform.World.Position}");
            return;
        }

        var doodad = DoodadManager.Instance.Create(caster.ParentWorld, 0, (uint)doodadId, caster, true);
        if (doodad == null)
        {
            Logger.Warn($"Special effects: SpawnDoodad could not create doodadId {doodadId}");
            return;
        }

        doodad.Transform = placementSource.Transform.CloneDetached(doodad);
        doodad.SetPosition(placementPosition.X, placementPosition.Y, placementPosition.Z, rpy.X, rpy.Y, rpy.Z);
        doodad.InitDoodad();
        if (delay > 0)
            Thread.Sleep(delay);
        doodad.Spawn();
    }

    internal static bool TryResolvePlacement(AiGeoDataManager geoData, Vector3 sourcePosition, float yawDegrees,
        DynamicDoodadPlacementPolicy placementPolicy, out Vector3 placementPosition)
    {
        var candidate = CreatePlacementCandidate(sourcePosition, yawDegrees);
        return DynamicDoodadPlacement.TryResolve(geoData, candidate, placementPolicy, out placementPosition);
    }

    internal static bool TryResolvePlacement(Vector3 sourcePosition, float yawDegrees,
        DynamicDoodadPlacementPolicy placementPolicy, DynamicDoodadPlacement.GroundSurfaceResolver surfaceResolver,
        out Vector3 placementPosition)
    {
        var candidate = CreatePlacementCandidate(sourcePosition, yawDegrees);
        return DynamicDoodadPlacement.TryResolve(candidate, placementPolicy, surfaceResolver, out placementPosition);
    }

    private static Vector3 CreatePlacementCandidate(Vector3 sourcePosition, float yawDegrees)
    {
        var (x, y) = MathUtil.AddDistanceToFrontDeg(1f, sourcePosition.X, sourcePosition.Y, yawDegrees + 90f);
        return new Vector3(x, y, sourcePosition.Z);
    }
}
