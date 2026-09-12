using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>Authored model geometry shared by placement queries in one server process.</summary>
public sealed class HousingGeometryAssets
{
    private readonly CryGeometryResolver _resolver;
    private readonly CryWorldGeometryResolver _staticResolver;
    private readonly Func<uint, uint, IReadOnlyList<string>> _modelPaths;
    private readonly Func<string, System.IO.Stream> _openFile;
    private readonly ConcurrentDictionary<uint, CryGeometryAsset> _models = [];
    private readonly ConcurrentDictionary<(WorldTemplate World, int X, int Y), Lazy<CryTerrainGrid>> _terrain = [];
    private readonly ConcurrentDictionary<WorldTemplate, Lazy<CryWorldObjectIndex>> _worlds = [];
    private readonly ConcurrentDictionary<WorldTemplate, Lazy<HousingWaterGeometry>> _water = [];

    public HousingGeometryAssets() : this(ClientFileManager.GetFileStream,
        (model, state) => HousingGeometryGameData.Instance.GetModelPaths(model, state)) { }

    public HousingGeometryAssets(Func<string, System.IO.Stream> openFile,
        Func<uint, uint, IReadOnlyList<string>> modelPaths)
    {
        _openFile = openFile;
        _resolver = new CryGeometryResolver(openFile);
        _staticResolver = new CryWorldGeometryResolver(Load);
        _modelPaths = modelPaths;
    }

    public CryGeometryAsset Load(string path) => _resolver.Load(path);

    public CryGeometryAsset LoadStatic(CryWorldObjectInstance instance) => _staticResolver.Load(instance);

    public CryGeometryAsset LoadModel(uint id) => _models.GetOrAdd(id, modelId => LoadModelPose(modelId, null));

    private CryGeometryAsset LoadModelPose(uint id, double? elapsedSeconds)
    {
        var paths = _modelPaths(id, 1);
        if (paths.Count == 0)
            throw new InvalidDataException($"No authored placement model for model {id}.");
        var assets = paths.Select(path => elapsedSeconds.HasValue ? _resolver.LoadPose(path, elapsedSeconds.Value) : Load(path)).ToArray();
        var bounds = assets.Where(asset => asset.HasModelBounds).Select(asset => (CryBounds?)asset.Bounds)
            .Aggregate((CryBounds?)null, (left, right) => left?.Union(right.Value) ?? right);
        return new CryGeometryAsset(bounds ?? new CryBounds(Vector3.Zero, Vector3.Zero),
            assets.SelectMany(asset => asset.Parts).ToArray())
        {
            HasModelBounds = bounds.HasValue,
            HasAnimatedCollision = assets.Any(asset => asset.HasAnimatedCollision),
            Helpers = assets.SelectMany(asset => asset.Helpers).ToArray(),
            PoseRequirements = assets.SelectMany(asset => asset.PoseRequirements).ToArray()
        };
    }

    public CryGeometryAsset LoadHouse(HousingTemplate template, int step = -1)
    {
        var modelId = step >= 0 && template.BuildSteps.TryGetValue(step, out var buildStep)
            ? buildStep.ModelId : template.MainModelId;
        return LoadModel(modelId);
    }

    public CryGeometryAsset LoadHouse(House house, DateTime utcNow)
    {
        var modelId = house.CurrentStep >= 0 && house.Template.BuildSteps.TryGetValue(house.CurrentStep, out var step)
            ? step.ModelId : house.Template.MainModelId;
        var asset = LoadModel(modelId);
        if (!asset.PoseRequirements.Any(pose => pose.Playing))
            return asset;
        return LoadModelPose(modelId, ElapsedSeconds(house.PlaceDate, utcNow));
    }

    public CryGeometryAsset LoadDoodad(DoodadTemplate template, uint phase = 0)
    {
        var path = DoodadModelPath(template, phase);
        // Native393b03b0 returns no model when both the requested path and base path are empty.
        return string.IsNullOrEmpty(path) ? null : Load(path);
    }

    private static string DoodadModelPath(DoodadTemplate template, uint phase)
    {
        var path = phase == 0 ? null : template.FuncGroups.FirstOrDefault(group => group.Id == phase)?.Model;
        if (string.IsNullOrEmpty(path))
            path = template.Model;
        return path;
    }

    public CryGeometryAsset LoadDoodad(Doodad doodad, DateTime utcNow)
    {
        var elapsedSeconds = ElapsedSeconds(doodad.PhaseTime, utcNow);
        var path = DoodadModelPath(doodad.Template, doodad.FuncGroupId);
        if (string.IsNullOrEmpty(path))
            return null;
        var animations = doodad.CurrentPhaseFuncs
            .Where(func => func.FuncType == nameof(DoodadFuncAnimate))
            .Select(func => DoodadManager.Instance.GetPhaseFuncTemplate(func.FuncId, func.FuncType))
            .OfType<DoodadFuncAnimate>().OrderBy(animation => animation.Id).ToArray();
        if (animations.Length == 0)
            return _resolver.LoadPose(path, elapsedSeconds);
        // The client picks one phase clip at random. Server collision uses the first authored ID.
        var animation = animations[0];
        // Native393a4360 uses CA_LOOP_ANIMATION (2) or CA_REPEAT_LAST_KEY (4).
        return _resolver.LoadAnimationPose(path, animation.Name,
            elapsedSeconds, !animation.PlayOnce);
    }

    private static double ElapsedSeconds(DateTime start, DateTime utcNow) =>
        start == DateTime.MinValue ? 0 : Math.Max(0, (utcNow - start).TotalSeconds);

    public CryWorldObjectIndex GetWorld(WorldTemplate world) => _worlds.GetOrAdd(world,
        template => new Lazy<CryWorldObjectIndex>(() => CryWorldObjectIndex.Load(template, _openFile,
            IncludesVegetationGeometry))).Value;

    private bool IncludesVegetationGeometry(string path)
    {
        try
        {
            return Load(path).Parts.Any(part =>
                CryGeometryLayerRules.GetVegetationUsage(part.PhysicsType) != CryGeometryQueryUsage.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            // Keep an unresolved model in the index so a query cannot silently pass through it.
            return true;
        }
    }

    public CryTerrainGrid GetTerrain(WorldTemplate world, int x, int y)
    {
        if (x < 0 || y < 0 || x >= world.Cells.GetLength(0) || y >= world.Cells.GetLength(1))
            return null;
        return _terrain.GetOrAdd((world, x, y), key => new Lazy<CryTerrainGrid>(() =>
        {
            var path = $"game/worlds/{key.World.Name}/cells/{key.X:000}_{key.Y:000}/client/terrain/heightmap.dat";
            using var stream = _openFile(path);
            return stream == null ? null : CryTerrainGrid.Read(stream,
                new Vector2(key.X * WorldManager.CELL_SIZE, key.Y * WorldManager.CELL_SIZE));
        })).Value;
    }

    public HousingWaterGeometry GetWater(WorldTemplate world) => _water.GetOrAdd(world,
        template => new Lazy<HousingWaterGeometry>(() => HousingWaterGeometry.Load(template, _openFile))).Value;

    public IReadOnlyList<CryWaterVolumeInstance> GetPrefabWater(WorldInstance world, IEnumerable<House> houses)
    {
        var instances = new List<(DateTime Time, uint ObjectId, CryWaterVolumeInstance Water)>();
        foreach (var doodad in world.GetAllDoodads())
        {
            if (doodad.Template == null)
                continue;
            var volumes = _resolver.LoadWater(DoodadModelPath(doodad.Template, doodad.FuncGroupId));
            if (volumes.Count > 0)
                instances.Add((doodad.PhaseTime, doodad.ObjId, new CryWaterVolumeInstance(volumes, Transform(doodad))));
        }
        foreach (var house in houses.Where(house => ReferenceEquals(house.ParentWorld, world) && house.Template != null))
        {
            var modelId = house.CurrentStep >= 0 && house.Template.BuildSteps.TryGetValue(house.CurrentStep, out var step)
                ? step.ModelId : house.Template.MainModelId;
            var volumes = _modelPaths(modelId, 1).SelectMany(_resolver.LoadWater).ToArray();
            if (volumes.Length > 0)
                instances.Add((house.PlaceDate, house.ObjId, new CryWaterVolumeInstance(volumes, Transform(house))));
        }
        // The client registers visible prefabs. The server uses persisted phase/placement order.
        return instances.OrderBy(instance => instance.Time).ThenBy(instance => instance.ObjectId)
            .Select(instance => instance.Water).ToArray();
    }

    public static Matrix4x4 Transform(BaseUnit unit)
    {
        var pose = unit.Transform.World;
        return Matrix4x4.CreateScale(unit.Scale) * Matrix4x4.CreateFromQuaternion(pose.ToQuaternion()) *
            Matrix4x4.CreateTranslation(pose.Position);
    }

    public static CryBox HouseBounds(CryBounds localBounds, Matrix4x4 transform)
    {
        var orientation = transform;
        orientation.Translation = Vector3.Zero;
        return new CryBox(Vector3.Transform(localBounds.Center, transform), localBounds.HalfSize, orientation);
    }

    public static CryBounds CollisionBounds(CryGeometryAsset asset, Matrix4x4 transform) =>
        asset.Parts.Aggregate(asset.Bounds.Transform(transform),
            (bounds, part) => bounds.Union(CryGeometryQueries.GetBounds(part, transform)));

    public static CryBounds GardenBounds(HousingTemplate template, CryBounds localBounds,
        Matrix4x4 transform, bool reserveAlley = true)
    {
        var bounds = localBounds.Transform(transform);
        var origin = transform.Translation;
        if (HousingFootprint.TryCreateGarden(new Vector2(origin.X, origin.Y), template.GardenRadius,
                reserveAlley ? template.Alley : 0, out var garden))
            bounds = new CryBounds(new Vector3(garden.MinX, garden.MinY, bounds.Min.Z),
                new Vector3(garden.MaxX, garden.MaxY, bounds.Max.Z));
        return new CryBounds(bounds.Min - new Vector3(0, 0, template.ExtraHeightBelow),
            bounds.Max + new Vector3(0, 0, template.ExtraHeightAbove));
    }
}
