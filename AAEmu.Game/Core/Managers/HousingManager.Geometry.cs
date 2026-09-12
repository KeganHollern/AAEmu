using System.Numerics;

using AAEmu.Game.GameData;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Core.Managers;

public partial class HousingManager
{
    internal HousingGeometryAssets GeometryAssets { get; set; } = new();

    private HousingGeometryWorld GeometryWorld(WorldInstance world, Func<Doodad, bool> ignoreDoodad = null,
        bool construction = false, bool includeStatic = true, DateTime? utcNow = null) =>
        new(GeometryAssets, world, () => construction ? [] : _houses.Values,
            bounds => OtherGeometry(world, bounds, construction), ignoreDoodad, includeStatic, utcNow);

    private IEnumerable<CryGeometryInstance> OtherGeometry(WorldInstance world, CryBounds bounds, bool construction)
    {
        foreach (var unit in world.GetAllUnits())
        {
            if (unit is House || unit.ModelId == 0)
                continue;
            // Native39181170 and39180530 ignore these logical unit types during construction.
            if (construction && unit.BaseUnitType is BaseUnitType.Character or BaseUnitType.Slave or BaseUnitType.Mate)
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
                asset = GeometryAssets.LoadModel(unit.ModelId);
                transform = HousingGeometryAssets.Transform(unit);
            }
            if (HousingGeometryAssets.CollisionBounds(asset, transform).Intersects(bounds))
                yield return new CryGeometryInstance(0, asset, transform, entityType);
        }
    }

    private ErrorMessageType CheckDecorationGeometry(Character player, House house, HousingDecoration design,
        Vector3 localPosition, Quaternion localRotation, uint supportObjId)
    {
        try
        {
            var template = doodadManager.GetTemplate(design.DoodadId);
            var decoration = template == null ? null : GeometryAssets.LoadDoodad(template);
            if (decoration?.HasModelBounds != true)
                return ErrorMessageType.HouseCannotDecorateSurface;
            var utcNow = DateTime.UtcNow;
            var houseAsset = GeometryAssets.LoadHouse(house, utcNow);
            if (!houseAsset.HasModelBounds)
                return ErrorMessageType.HouseCannotDecorateSurface;
            var houseTransform = HousingGeometryAssets.Transform(house);
            var transform = Matrix4x4.CreateFromQuaternion(localRotation) *
                Matrix4x4.CreateTranslation(localPosition) * houseTransform;
            var garden = HousingGeometryAssets.GardenBounds(house.Template, houseAsset.Bounds, houseTransform);
            if (!HousingDecorationGeometry.IsWithinSelectionRange(garden, player.Transform.World.Position))
                return ErrorMessageType.TooFarAway;
            var scene = GeometryWorld(house.ParentWorld, utcNow: utcNow);
            // Native3903da00 aligns local +Z with the ray-hit surface normal. The packet sends its exact pivot.
            // One centimetre allows float coordinate composition at world scale, without a placement offset.
            const float surfaceTolerance = 0.01f;
            var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, transform));
            var pivot = transform.Translation;
            var supportResult = scene.Raycast(pivot + normal * surfaceTolerance,
                -normal * (2 * surfaceTolerance), out var support);
            if (supportResult != CryIntersection.Intersects || support.PickingIndex > 0 ||
                support.ObjectId != supportObjId || Vector3.DistanceSquared(pivot, support.Hit.Position) > surfaceTolerance * surfaceTolerance ||
                Vector3.Dot(normal, support.Hit.Normal) < 0.999f)
                return ErrorMessageType.HouseCannotDecorateSurface;
            var supportAllowed = supportObjId == 0 || template.Childable &&
                house.ParentWorld.GetDoodad(supportObjId)?.Template?.Parentable == true;
            return HousingDecorationGeometry.Check(design, decoration.Bounds, transform,
                HousingGeometryAssets.HouseBounds(houseAsset.Bounds, houseTransform), garden,
                house.Template.GardenRadius > 0, support.Hit.Normal, supportAllowed,
                (origin, vector) =>
                {
                    var result = scene.Raycast(origin, vector, out var hit);
                    return new HousingDecorationRayResult(result, hit.IsTerrain);
                }, scene.IntersectBox);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            Logger.Warn(exception, "Cannot resolve decoration geometry for house {0}, decoration {1}", house.Id, design.Id);
            return ErrorMessageType.HouseCannotDecorateSurface;
        }
    }

    private HousingConstructionPose ResolveConstructionGeometry(Character player, HousingTemplate template,
        Vector3 position, float yaw)
    {
        try
        {
            // Dominion structures need the separate siege authority and connector lifecycle from issue 143.
            if (template.CategoryId is 2 or 3 or 4 or 5 or 14)
                return new(position, yaw, ErrorMessageType.HouseCannotLocateNotDominatedZone);
            var asset = GeometryAssets.LoadHouse(template);
            if (!asset.HasModelBounds)
                return new(position, yaw, ErrorMessageType.HouseCannotLocateInvalidArea);
            var utcNow = DateTime.UtcNow;
            bool IgnoreDoodad(Doodad doodad) => doodad.ParentObjId != 0 ||
                CommonFarmGameData.Instance.IsRemovedByHouse(doodad.Template.GroupId) ||
                GetHouseAtLocation(player.ParentWorld, doodad.Transform.World.Position, utcNow) != null;
            var scene = GeometryWorld(player.ParentWorld, IgnoreDoodad, construction: true, utcNow: utcNow);
            var (_, _, encodedYaw) = PositionAndRotation.ToRollPitchYawSBytes(new Vector3(0, 0, yaw));
            yaw = PositionAndRotation.FromRollPitchYawSBytes(0, 0, encodedYaw).Z;
            var pose = HousingConstructionGeometry.Resolve(template, position, yaw, player.Transform.World.Position,
                asset.Bounds.Min, asset.Bounds.Max, scene.SampleElevation, scene.SampleRawHeight, scene.GetTerrainUnitSize(position));
            if (!pose.IsValid)
                return pose;
            var transform = Matrix4x4.CreateRotationZ(pose.Yaw) * Matrix4x4.CreateTranslation(pose.Position);
            if (!HousingConstructionGeometry.IsPlotInsideAreas(template, pose.Position,
                point => player.ParentWorld.Template.HousingZones.Values.SelectMany(areas => areas)
                    .FirstOrDefault(area => area.Group == 1 && area.Contains(point))))
                return pose with { Error = ErrorMessageType.HouseCannotLocateInvalidArea };
            if (template.GardenRadius > 0)
            {
                foreach (var cell in HousingFootprint.GetCells(new Vector2(pose.Position.X, pose.Position.Y), template.GardenRadius, template.Alley))
                foreach (var corner in cell.Corners(0))
                {
                    var ground = scene.SampleRawHeight((int)corner.X, (int)corner.Y);
                    var point = corner with { Z = ground };
                    var waterError = HousingConstructionGeometry.CheckWater(template.CategoryId, ground,
                        GeometryAssets.GetWater(player.ParentWorld.Template).GetWaterLevel(point, player.ParentWorld.Water.OceanLevel));
                    if (waterError != ErrorMessageType.NoErrorMessage)
                        return pose with { Error = waterError };
                }
            }
            else
            {
                var waterError = HousingConstructionGeometry.CheckWater(template.CategoryId, pose.Position.Z,
                    GeometryAssets.GetWater(player.ParentWorld.Template).GetWaterLevel(pose.Position, player.ParentWorld.Water.OceanLevel));
                if (waterError != ErrorMessageType.NoErrorMessage)
                    return pose with { Error = waterError };
            }
            foreach (var neighbor in _houses.Values.Where(neighbor => ReferenceEquals(neighbor.ParentWorld, player.ParentWorld)))
            {
                var neighborAsset = GeometryAssets.LoadHouse(neighbor, utcNow);
                if (!neighborAsset.HasModelBounds)
                    return pose with { Error = ErrorMessageType.HouseCannotLocateInvalidArea };
                if (HousingConstructionGeometry.OverlapsHouse(template, asset.Bounds, transform,
                        neighbor.Template, neighborAsset.Bounds, HousingGeometryAssets.Transform(neighbor)))
                    return pose with { Error = ErrorMessageType.HouseCannotLocateOverlapHouse };
            }
            var collision = scene.IntersectBox(HousingGeometryAssets.HouseBounds(asset.Bounds, transform));
            if (collision != CryIntersection.Clear)
                return pose with { Error = ErrorMessageType.HouseCannotLocateOverlapUnit };
            if (template.GardenRadius > 0)
            {
                var garden = HousingGeometryAssets.GardenBounds(template, asset.Bounds, transform);
                // Native39180530 checks logical units and unbound doodads in the garden cells.
                // Static world geometry participates in the model OBB query above, not this cell query.
                var gardenScene = GeometryWorld(player.ParentWorld, IgnoreDoodad, construction: true, includeStatic: false, utcNow: utcNow);
                if (gardenScene.IntersectBox(new CryBox(garden.Center, garden.HalfSize, Matrix4x4.Identity)) != CryIntersection.Clear)
                    return pose with { Error = ErrorMessageType.HouseCannotLocateOverlapUnit };
            }
            return pose;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            Logger.Warn(exception, "Cannot resolve construction geometry for design {0}", template.Id);
            return new(position, yaw, ErrorMessageType.HouseCannotLocateInvalidArea);
        }
    }
}
