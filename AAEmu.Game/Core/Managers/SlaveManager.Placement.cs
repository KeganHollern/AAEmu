using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Core.Managers;

public partial class SlaveManager
{
    internal HousingGeometryAssets PlacementAssets { get; set; } = new();
    internal Func<Character, SlaveTemplate, SlavePlacementRequest, ErrorMessageType> PlacementGeometry { get; set; }

    public bool Create(Character owner, SkillItem skillData, SlavePlacementRequest request)
    {
        var error = CheckPlacement(owner, skillData, request, out var template);
        if (error == ErrorMessageType.NoErrorMessage)
            error = CheckPlacementGeometry(owner, template, request);
        if (error != ErrorMessageType.NoErrorMessage)
        {
            owner?.SendErrorMessage(error);
            return false;
        }

        // Resolve the whole request before the existing replacement path removes a vehicle,
        // allocates IDs, writes item details, or sends spawn packets.
        using var position = new Transform(null) { InstanceId = World.Id };
        position.Local.SetPosition(request.Position, new Vector3(0, 0, request.Yaw));
        return Create(owner, skillData, false, position);
    }

    internal static bool TryMeasureWreckFloor(WorldInstance world, Vector3 position, out float height)
    {
        height = default;
        var assets = world.SlaveManager?.PlacementAssets;
        var template = world.Template;
        try
        {
            var cellX = (int)MathF.Floor(position.X / WorldManager.CELL_SIZE);
            var cellY = (int)MathF.Floor(position.Y / WorldManager.CELL_SIZE);
            if (assets?.GetTerrain(template, cellX, cellY)?.IsHole(position.X, position.Y) == true)
                return TryGetHoleFloor(assets, template, position, out height);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            NotSupportedException or ArgumentException or OverflowException)
        {
            Logger.Warn(exception, $"Cannot measure wreck support at {position} in {template.Name}");
            return false;
        }

        if (!template.GeoData.TryGetGroundSurface(position, out var ground) ||
            ground.Decision == GroundSurfaceDecision.TerrainUnavailableFallback)
            return false;
        height = ground.Height;
        return true;
    }

    private static bool TryGetHoleFloor(HousingGeometryAssets assets, WorldTemplate world, Vector3 position, out float height)
    {
        // The raw terrain and navigation can both describe empty space inside a cutout.
        // Inspect higher bounds first so objects entirely below the measured support need no geometry.
        height = float.NegativeInfinity;
        var bottom = new Vector3(position.X, position.Y, float.MinValue);
        foreach (var instance in assets.GetWorld(world).Query(bottom, position).OrderByDescending(x => x.Max.Z))
        {
            if (instance.Max.Z <= height)
                break;
            var asset = assets.LoadStatic(instance);
            if (!asset.Parts.Any(part => part.PhysicsType == CryGeometryLayerRules.Solid))
                continue;
            if (CryGeometryScene.HasUnresolvedCollisionPose(asset))
                return false;

            var transform = CryVegetationGeometry.ResolveTransform(instance, (x, y) =>
                assets.GetTerrain(world, (int)MathF.Floor(x / WorldManager.CELL_SIZE),
                    (int)MathF.Floor(y / WorldManager.CELL_SIZE))?.SampleHeight(x, y) ?? float.NaN);
            foreach (var part in asset.Parts)
            {
                if (part.PhysicsType != CryGeometryLayerRules.Solid)
                    continue;
                var length = position.Z - Math.Max(height, instance.Min.Z);
                if (CryGeometryQueries.Raycast(part, transform, position, -Vector3.UnitZ, MathF.BitIncrement(length),
                    true, out var hit) && hit.Normal.Z > 0f)
                    height = Math.Max(height, hit.Position.Z);
            }
        }
        return float.IsFinite(height);
    }

    internal ErrorMessageType CheckPlacement(Character owner, SkillItem skillData, SlavePlacementRequest request,
        out SlaveTemplate template)
    {
        template = null;
        if (!IsValidPlacementRequest(owner, request))
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        var item = ZoneSkillRestrictions.GetSourceItem(owner, skillData);
        if (item is not SummonSlave scroll || item.Template is not SummonSlaveTemplate itemTemplate ||
            item.OwnerId != owner.Id || item.Count < 1)
            return ErrorMessageType.NotEnoughItem;
        template = SlaveGameData.Instance.GetSlaveTemplate(itemTemplate.SlaveId);
        if (template == null)
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        if (ZoneSkillRestrictions.GetItemBan(owner, item, request.Position) != null)
            return ErrorMessageType.ItemCannotUseHere;
        // Native39349f00 checks this saved scroll location only when there is no active local vehicle.
        if (GetActiveSlaveByOwnerObjId(owner.ObjId) == null && scroll.HasSummonLocation &&
            !SlavePlacementRules.IsWithinSavedLocationRange(owner.Transform.World.Position,
                scroll.SummonLocation, template.SpawnValidAreaRance))
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        return ErrorMessageType.NoErrorMessage;
    }

    private bool IsValidPlacementRequest(Character owner, SlavePlacementRequest request) =>
        owner?.ParentWorld?.Template != null && ReferenceEquals(owner.ParentWorld, World) &&
        SlavePlacementRules.IsWithinRange(owner.Transform.World.Position, request.Position) &&
        float.IsFinite(request.Yaw) && request.Position.X >= 0 && request.Position.Y >= 0 &&
        request.Position.X < World.Template.CellX * WorldManager.CELL_SIZE &&
        request.Position.Y < World.Template.CellY * WorldManager.CELL_SIZE;

    private ErrorMessageType CheckPlacementGeometry(Character owner, SlaveTemplate template, SlavePlacementRequest request,
        Slave movingVehicle = null)
    {
        if (PlacementGeometry != null)
            return PlacementGeometry(owner, template, request);
        try
        {
            var modelType = ModelManager.Instance.GetModelType(template.ModelId)?.SubType;
            if (modelType is not "ShipModel" and not "VehicleModel")
                return ErrorMessageType.SlaveSpawnErrorInvalidArea;
            var model = PlacementAssets.LoadModel(template.ModelId);
            if (!model.HasModelBounds)
                return ErrorMessageType.SlaveSpawnErrorInvalidArea;
            var utcNow = DateTime.UtcNow;
            // Dynamic client physics contains the objects published from this neighborhood.
            // Resolve assets only inside that scope; a remote unsupported model is unrelated.
            var objects = GetPlacementObjects(World, owner.Transform.World.Position);
            if (movingVehicle != null)
                objects = objects.Where(obj => !MovesWith(obj, movingVehicle)).ToArray();
            var houses = objects.OfType<House>().ToArray();
            var visibleDoodads = objects.OfType<Doodad>().ToArray();
            var doodads = visibleDoodads.Select(doodad => doodad.ObjId).ToHashSet();
            var units = new Lazy<CryGeometryInstance[]>(() => PlacementUnits(objects.OfType<Unit>()).ToArray());
            var scene = new HousingGeometryWorld(PlacementAssets, World, () => houses,
                bounds => QueryPlacementUnits(units.Value, bounds),
                ignoreDoodad: doodad => !doodads.Contains(doodad.ObjId), utcNow: utcNow);
            // Native39347fb0 skips the caster's attached unit physics only for the line query.
            var ignoredObject = owner.Transform.Parent?.GameObject?.ObjId ??
                owner.Transform.StickyParent?.GameObject?.ObjId ?? 0;
            var terrain = scene.SampleElevation(request.Position.X, request.Position.Y);
            var boat = modelType == "ShipModel";
            var oceanWaveSize = 0f;
            var surface = boat
                ? PlacementAssets.GetWater(World.Template).GetWaterLevel(request.Position, World.Water.OceanLevel,
                    PlacementAssets.GetPrefabWater(World, houses, visibleDoodads), out oceanWaveSize)
                : terrain;
            // An authored hole is not an available land surface. Raised floors and transfer
            // attachments need their own policy; this skill path uses the native terrain elevation.
            if (!boat && !float.IsFinite(scene.SampleHeight(request.Position.X, request.Position.Y)))
                return ErrorMessageType.SlaveSpawnErrorInvalidArea;
            return SlavePlacementRules.CheckGeometry(owner.Transform.World.Position, request, model.Bounds,
                boat, terrain, surface, scene.IntersectBox, (origin, end) =>
                {
                    var result = scene.Raycast(origin, end - origin, out var hit, ignoredObject);
                    // The selected ground surface is the endpoint, not an obstruction before it.
                    return result == CryIntersection.Intersects &&
                        hit.Hit.Distance >= Vector3.Distance(origin, end) - SlavePlacementRules.SurfaceTolerance
                        ? CryIntersection.Clear : result;
                }, oceanWaveSize);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or
                                          ArgumentException or OverflowException)
        {
            Logger.Warn(exception, "Cannot resolve placement geometry for vehicle {0}", template.Id);
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        }
    }

    internal static GameObject[] GetPlacementObjects(WorldInstance world, Vector3 position)
    {
        var region = world.GetRegionByPos(position);
        if (region == null)
            throw new InvalidDataException("No active region for vehicle placement.");
        var objects = new List<GameObject>();
        foreach (var neighbor in region.GetNeighbors())
            neighbor?.GetList(objects, 0);
        return objects.Distinct().ToArray();
    }

    private static bool MovesWith(GameObject obj, Slave vehicle)
    {
        for (var transform = obj.Transform; transform != null; transform = transform.Parent ?? transform.StickyParent)
            if (ReferenceEquals(transform.GameObject, vehicle))
                return true;
        return false;
    }

    internal bool TryEscapePlacement(Character owner, Slave vehicle, SkillCastTarget target,
        out SlavePlacementRequest request)
    {
        var error = CheckEscape(owner, vehicle, target, out request);
        if (error == ErrorMessageType.NoErrorMessage)
            return true;
        owner.SendErrorMessage(error);
        return false;
    }

    private ErrorMessageType CheckEscape(Character owner, Slave vehicle, SkillCastTarget target,
        out SlavePlacementRequest request)
    {
        request = default;
        if (vehicle.AttachmentsRetired || !vehicle.IsVisible || !ReferenceEquals(vehicle.ParentWorld, World) ||
            !ReferenceEquals(vehicle.Summoner, owner) || !ReferenceEquals(owner.ParentWorld, World) ||
            !ReferenceEquals(GetActiveSlaveByOwnerObjId(owner.ObjId), vehicle))
            return ErrorMessageType.InvalidTarget;
        if (vehicle.Template.IsABoat() && (World.Physics == null || vehicle.RigidBody == null || vehicle.ShipController == null))
            return ErrorMessageType.InvalidTarget;
        var distance = SkillRange.GetDistance(owner, vehicle);
        if (!float.IsFinite(distance) || distance > 20)
            return ErrorMessageType.SlaveEscapeTooFarFromSlave;
        if (!SlavePlacementRequest.TryReadTarget(target, out request) || !IsValidPlacementRequest(owner, request))
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        return CheckPlacementGeometry(owner, vehicle.Template, request, vehicle);
    }

    internal bool MoveEscapingVehicle(Character owner, Slave vehicle, SkillCastTarget target)
    {
        if (vehicle.Template.IsABoat())
            return World.Physics?.MoveEscapingShip(owner, vehicle, target) == true;

        lock (vehicle.AttachmentSyncRoot)
        {
            if (!TryEscapePlacement(owner, vehicle, target, out var request))
                return false;

            var driver = vehicle.AttachedCharacters.GetValueOrDefault(AttachPointKind.Driver);
            if (driver != null)
                vehicle.DriverEscapeDeparture = vehicle.Transform.World.Position;
            vehicle.Transform.Local.SetPosition(request.Position, new Vector3(0, 0, request.Yaw));
            vehicle.VehicleVelocity = Vector3.Zero;
            vehicle.Transform.FinalizeTransform();
            MovementValidation.RecordServerRelocation(vehicle);

            // Land drivers use this movement type for the cart and its attached seats.
            // Crossing a visibility boundary publishes the normal entry or departure first.
            var rotation = vehicle.Transform.World.ToQuaternion();
            var (roll, pitch, yaw) = PositionAndRotation.ToRollPitchYawShorts(rotation.W < 0 ? -rotation : rotation);
            var movement = new VehicleMoveType
            {
                Type = MoveTypeEnum.Vehicle,
                Time = (uint)(DateTime.UtcNow - vehicle.SpawnTime).TotalMilliseconds,
                X = request.Position.X, Y = request.Position.Y, Z = request.Position.Z,
                RotationX = roll, RotationY = pitch, RotationZ = yaw
            };
            vehicle.BroadcastPacket(new SCOneUnitMovementPacket(vehicle.ObjId, movement), false);
            if (driver != null)
            {
                // The seated client simulates its own cart. Reload that actor using the normal
                // visibility and seat state so its next drive starts at the new position.
                driver.SendPacket(new SCUnitsRemovedPacket([vehicle.ObjId]));
                vehicle.AddVisibleObject(driver);
                driver.SendPacket(new SCSlaveBoundPacket(driver.Id, vehicle.ObjId));
                if (ReferenceEquals(driver.CurrentTarget, vehicle))
                    driver.SendPacket(new SCTargetChangedPacket(driver.ObjId, vehicle.ObjId));
            }
            return true;
        }
    }

    internal static IEnumerable<CryGeometryInstance> QueryPlacementUnits(IEnumerable<CryGeometryInstance> units,
        CryBounds bounds)
    {
        foreach (var unit in units)
        {
            if (!HousingGeometryAssets.CollisionBounds(unit.Asset, unit.Transform).Intersects(bounds))
                continue;
            // Some native vehicles create physics at runtime and have no authored collision parts.
            // Their bounds limit the unavailable area; do not treat that area as empty space.
            yield return unit.Asset.Parts.Count == 0 ? unit with { HasUnresolvedCollision = true } : unit;
        }
    }

    private IEnumerable<CryGeometryInstance> PlacementUnits(IEnumerable<Unit> units)
    {
        foreach (var unit in units)
        {
            if (unit is House || unit.ModelId == 0)
                continue;
            var actor = ModelManager.Instance.GetActorModel(unit.ModelId);
            CryGeometryAsset asset;
            Matrix4x4 transform;
            var entityType = 1;
            if (actor != null)
            {
                var stanceId = unit is Npc npc ? npc.CurrentGameStance : unit.CollisionStance;
                if (!actor.Stances.TryGetValue(stanceId, out var stance))
                    throw new InvalidDataException($"No collision stance {stanceId} for actor {unit.ModelId}.");
                asset = CryActorGeometry.Create(stance, unit.Scale);
                transform = Matrix4x4.CreateTranslation(unit.Transform.World.Position);
                entityType = 8;
            }
            else
            {
                asset = PlacementAssets.LoadModel(unit.ModelId);
                if (!asset.HasModelBounds || !SlavePlacementRules.ValidBounds(asset.Bounds))
                    throw new InvalidDataException($"No model bounds for nearby unit {unit.ModelId}.");
                transform = HousingGeometryAssets.Transform(unit);
            }
            yield return new CryGeometryInstance(unit.ObjId, asset, transform, entityType);
        }
    }
}
