using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
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

        if (!ZoneSkillRestrictions.CanApply(caster, skill, casterObj))
            return;

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
            SkillLaborBatch.Current?.Fail();
            return;
        }

        if (!ZoneSkillRestrictions.CanApply(caster, skill, casterObj, placementPosition))
            return;

        // The two wind effects share a cast, but each has its own one-second publication delay.
        if (skill?.Template.Id == 11592 && caster is Character player && doodadId is 679 or 1203 && delay == 1000)
        {
            var pose = new PositionAndRotation(placementPosition, target.Transform.World.Rotation);
            var task = new WindSpawnTask(caster.ParentWorld, player, (uint)doodadId, placementSource.Transform.ZoneId, pose);
            void Schedule() => TaskManager.Instance.Schedule(task, TimeSpan.FromMilliseconds(delay));
            if (SkillLaborBatch.Current is { } batch)
                batch.AfterCommit(Schedule);
            else
                Schedule();
            return;
        }

        var doodad = DoodadManager.Instance.Create(caster.ParentWorld, 0, (uint)doodadId, caster, true);
        if (doodad == null)
        {
            Logger.Warn($"Special effects: SpawnDoodad could not create doodadId {doodadId}");
            SkillLaborBatch.Current?.Fail();
            return;
        }

        doodad.Transform = placementSource.Transform.CloneDetached(doodad);
        doodad.Transform.Local.SetPosition(placementPosition.X, placementPosition.Y, placementPosition.Z, rpy.X, rpy.Y, rpy.Z);
        DoodadCreation.InitializeAndSpawnTemporary(doodad, delay);
    }

    private sealed class WindSpawnTask(WorldInstance world, Character caster, uint templateId, uint zoneId, PositionAndRotation pose) : Tasks.Task
    {
        public override void Execute()
        {
            lock (SaveManager.PersistenceSyncRoot)
            {
                if (!WorldManager.Instance.IsRegistered(world) || caster.ParentWorld != world || world.GetUnit(caster.ObjId) != caster)
                    return;
                var doodad = DoodadManager.Instance.Create(world, 0, templateId, caster, true);
                if (doodad == null)
                    return;
                doodad.Transform = new Transform(doodad, null, zoneId, world.Id, pose);
                doodad.Spawn();
                doodad.InitDoodad();
            }
        }
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
