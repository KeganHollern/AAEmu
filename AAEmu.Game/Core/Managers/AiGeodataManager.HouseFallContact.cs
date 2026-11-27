using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    private sealed record HouseModel(CryGeometryAsset Asset, CryBounds Bounds, long RetryAfter = 0);
    private sealed record HouseFloor(HousingTemplate Template, Vector3 Origin, Vector3 Rotation, float Scale,
        CryBounds Bounds, Lazy<FallFaces> Faces);
    private readonly ConditionalWeakTable<HousingTemplate, HouseModel> _houseModels = new();
    private readonly ConditionalWeakTable<House, HouseFloor> _houseFloors = new();

    internal bool HasFinishedHouseFallSupport(Unit actor, Vector3 foot)
    {
        var region = actor.Region;
        if (region == null)
            return false;

        foreach (var neighbor in region.GetNeighbors())
        {
            if (neighbor == null)
                continue;
            foreach (var house in neighbor.Houses)
            {
                if (!ReferenceEquals(house.ParentWorld, actor.ParentWorld) || house.CurrentStep != -1 ||
                    house.OwnerId == 0 || house.Template == null || house.ModelId != house.Template.MainModelId)
                    continue;
                if (HasContactOnHouse(house, foot, actor is Character))
                    return true;
            }
        }
        return false;
    }

    private bool HasContactOnHouse(House house, Vector3 foot, bool playerInclines)
    {
        var pose = house.Transform.World;
        var template = house.Template;
        if (!_houseFloors.TryGetValue(house, out var floor) || floor.Template != template ||
            floor.Origin != pose.Position || floor.Rotation != pose.Rotation || floor.Scale != house.Scale)
        {
            var model = GetHouseModel(template);
            if (model.Asset == null)
                return false;
            floor = PlaceHouseFloor(house, model);
            _houseFloors.AddOrUpdate(house, floor);
        }
        return floor.Bounds.Contains(foot) && TouchesFallFace(floor.Faces.Value, foot, foot - floor.Origin, playerInclines);
    }

    private static HouseFloor PlaceHouseFloor(House house, HouseModel model)
    {
        var placement = HousingGeometryAssets.Transform(house);
        var bounds = model.Bounds.Transform(placement);
        var margin = Vector3.UnitZ * FallMovement.PositionTolerance;
        return new HouseFloor(house.Template, placement.Translation, house.Transform.World.Rotation, house.Scale,
            new CryBounds(bounds.Min - margin, bounds.Max + margin),
            new Lazy<FallFaces>(() => ReadFallFaces(placement, model.Asset, includeBoxes: false)));
    }

    private HouseModel GetHouseModel(HousingTemplate template)
    {
        if (_houseModels.TryGetValue(template, out var model) &&
            (model.RetryAfter == 0 || Environment.TickCount64 < model.RetryAfter))
            return model;
        try
        {
            var asset = HousingManager.Instance.GeometryAssets.LoadHouse(template);
            model = asset.HasModelBounds
                ? new HouseModel(asset, HousingGeometryAssets.CollisionBounds(asset, Matrix4x4.Identity))
                : new HouseModel(null, default);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            // A missing model cannot prove contact. Share the retry across houses that use the same template.
            Logger.Debug(exception, "Could not measure the floor model for housing template {0}", template.Id);
            model = new HouseModel(null, default, Environment.TickCount64 + 30_000);
        }
        _houseModels.AddOrUpdate(template, model);
        return model;
    }
}
