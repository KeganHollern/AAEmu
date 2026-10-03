using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.Core.Managers;

public partial class ShipyardManager
{
    internal HousingGeometryAssets PlacementAssets { get; set; } = new();
    private Dictionary<uint, uint> _designShipyards = [];
    internal Func<Character, ShipyardsTemplate, ShipyardPlacementRequest, ErrorMessageType> PlacementGeometry { get; set; }

    public Shipyard Create(Character owner, ShipyardPlacementRequest request)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var error = CheckPlacement(owner, request, out var template);
            if (error == ErrorMessageType.NoErrorMessage)
                error = (PlacementGeometry ?? CheckPlacementGeometry)(owner, template, request);
            if (error != ErrorMessageType.NoErrorMessage)
            {
                owner?.SendErrorMessage(error);
                return null;
            }
            return Create(owner, new ShipyardData
            {
                Id = request.DesignItemId, TemplateId = template.Id, Step = 0,
                X = request.Position.X, Y = request.Position.Y, Z = request.Position.Z, zRot = request.Yaw
            });
        }
    }

    internal ErrorMessageType CheckPlacement(Character owner, ShipyardPlacementRequest request, out ShipyardsTemplate template)
    {
        template = null;
        if (owner?.ParentWorld?.Template == null || owner.Inventory?.Bag == null ||
            !ShipyardPlacementRules.IsFinite(request.Position) || !float.IsFinite(request.Yaw) ||
            !ShipyardPlacementRules.ValidBounds(request.LocalBounds) ||
            request.Position.X < 0 || request.Position.Y < 0 ||
            !_shipyardsTemplate.TryGetValue(request.TemplateId, out template) ||
            !template.ShipyardSteps.ContainsKey(0))
            return ErrorMessageType.CraftLocatingUnitIsNotExist;
        var design = owner.Inventory.Bag.GetItemByItemId(request.DesignItemId);
        if (design == null || design.OwnerId != owner.Id || design.TemplateId != template.OriginItemId || design.Count < 1 ||
            !_designShipyards.TryGetValue(design.TemplateId, out var mappedTemplate) || mappedTemplate != template.Id)
            return ErrorMessageType.NotEnoughItem;
        if (!ShipyardPlacementRules.IsWithinRange(owner.Transform.World.Position, request.Position))
            return ErrorMessageType.TooFarAway;
        var world = owner.ParentWorld;
        if (world.Template.Id != WorldManager.DefaultWorldTemplateId || world.Id != WorldManager.DefaultInstanceId ||
            request.Position.X >= world.Template.CellX * WorldManager.CELL_SIZE ||
            request.Position.Y >= world.Template.CellY * WorldManager.CELL_SIZE)
            return ErrorMessageType.CraftLocatingUnitIsNotOnTheWaterOrDeepWater;
        var zone = ZoneManager.Instance.GetZoneByKey(worldManager.GetZoneId(world.Template, request.Position.X, request.Position.Y));
        if (zone == null || zone.Closed)
            return ErrorMessageType.CraftLocatingUnitIsNotOnTheWaterOrDeepWater;
        return ZoneSkillRestrictions.GetItemBan(owner, design, request.Position) == null
            ? ErrorMessageType.NoErrorMessage : ErrorMessageType.ItemCannotUseHere;
    }

    private ErrorMessageType CheckPlacementGeometry(Character owner, ShipyardsTemplate template, ShipyardPlacementRequest request)
    {
        try
        {
            var world = owner.ParentWorld;
            var model = PlacementAssets.LoadModel(template.ShipyardSteps[0].ModelId);
            if (!model.HasModelBounds)
                return ErrorMessageType.CraftLocatingUnitIsNotExist;
            var utcNow = DateTime.UtcNow;
            var houses = world.GetAllUnits().OfType<House>().ToArray();
            foreach (var house in houses)
            {
                var houseModel = PlacementAssets.LoadHouse(house, utcNow);
                if (!houseModel.HasModelBounds || !ShipyardPlacementRules.ValidBounds(houseModel.Bounds))
                    throw new InvalidDataException($"No placement bounds for neighboring house {house.Id}.");
            }
            var scene = new HousingGeometryWorld(PlacementAssets, world, () => houses,
                bounds => PlacementUnits(world, bounds), utcNow: utcNow);
            var surfacePoint = request.Position - Vector3.UnitZ * ShipyardPlacementRules.PreviewHeight;
            var water = PlacementAssets.GetWater(world.Template).GetWaterLevel(surfacePoint, world.Water.OceanLevel,
                PlacementAssets.GetPrefabWater(world, houses));
            var ground = scene.SampleHeight(request.Position.X, request.Position.Y);
            if (!float.IsFinite(ground) || !float.IsFinite(water) || ground >= water)
                return ErrorMessageType.CraftLocatingUnitIsNotOnTheWaterOrDeepWater;
            // The request's box never supplies collision authority. Read the preview model from server assets.
            var neighbors = _shipyard.Values.Where(other => ReferenceEquals(other.ParentWorld, world))
                .Select(NeighborBox);
            return ShipyardPlacementRules.CheckGeometry(model.Bounds, request.Position, request.Yaw,
                template.BuildRadius, water, neighbors, scene.IntersectBox);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            Logger.Warn(exception, "Cannot resolve placement geometry for shipyard {0}", template.Id);
            return ErrorMessageType.CraftLocatingUnitIsNotExist;
        }
    }

    private CryBox NeighborBox(Shipyard other)
    {
        var model = PlacementAssets.LoadModel(other.ModelId);
        if (!model.HasModelBounds || !ShipyardPlacementRules.ValidBounds(model.Bounds))
            throw new InvalidDataException($"No placement bounds for neighboring shipyard {other.ShipyardData.Id}.");
        return ShipyardPlacementRules.ShipyardBox(model.Bounds,
            other.Transform.World.Position, other.Transform.World.Rotation.Z, 0);
    }

    internal static Dictionary<uint, uint> ReadDesignShipyards(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id,shipyard_id FROM item_shipyards";
        using var reader = command.ExecuteReader();
        var mappings = new Dictionary<uint, uint>();
        while (reader.Read())
        {
            var item = checked((uint)reader.GetInt64(0));
            var template = checked((uint)reader.GetInt64(1));
            if (item == 0 || template == 0 || !mappings.TryAdd(item, template))
                throw new InvalidDataException("Invalid or duplicate shipyard design mapping.");
        }
        return mappings;
    }

    private IEnumerable<CryGeometryInstance> PlacementUnits(WorldInstance world, CryBounds bounds)
    {
        foreach (var unit in world.GetAllUnits())
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
                transform = HousingGeometryAssets.Transform(unit);
            }
            if (HousingGeometryAssets.CollisionBounds(asset, transform).Intersects(bounds))
                yield return new CryGeometryInstance(unit.ObjId, asset, transform, entityType);
        }
    }
}
