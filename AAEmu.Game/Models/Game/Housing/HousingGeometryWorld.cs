using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>Combines authored world geometry and the current persisted world objects.</summary>
public sealed class HousingGeometryWorld(HousingGeometryAssets assets, WorldInstance world,
    Func<IEnumerable<House>> houses, Func<CryBounds, IEnumerable<CryGeometryInstance>> otherInstances,
    Func<Doodad, bool> ignoreDoodad = null, bool includeStatic = true)
{
    private readonly CryGeometryScene _scene = new(bounds => QueryInstances(assets, world, houses,
        otherInstances, ignoreDoodad, includeStatic, bounds), _ => true);

    private static IEnumerable<CryGeometryInstance> QueryInstances(HousingGeometryAssets geometry,
        WorldInstance instance, Func<IEnumerable<House>> getHouses,
        Func<CryBounds, IEnumerable<CryGeometryInstance>> getOtherInstances, Func<Doodad, bool> ignored,
        bool includeStatic, CryBounds bounds)
    {
        foreach (var authored in includeStatic
                     ? geometry.GetWorld(instance.Template).Query(bounds.Min, bounds.Max) : [])
        {
            var transform = CryVegetationGeometry.ResolveTransform(authored, (x, y) =>
                geometry.GetTerrain(instance.Template, Cell(x), Cell(y))?.SampleHeight(x, y) ?? float.NaN);
            var asset = geometry.LoadStatic(authored);
            if (!string.IsNullOrEmpty(authored.MaterialPath))
                asset = asset with { Parts = asset.Parts.Select(part => part with
                {
                    MaterialPath = authored.MaterialPath
                }).ToArray() };
            yield return new CryGeometryInstance(0, asset, transform)
            {
                IsVegetation = authored.Kind == ObjectDataType.Vegetation
            };
        }
        foreach (var house in getHouses().Where(house => ReferenceEquals(house.ParentWorld, instance)))
        {
            var asset = geometry.LoadHouse(house.Template, house.CurrentStep);
            var transform = HousingGeometryAssets.Transform(house);
            if (asset.Bounds.Transform(transform).Intersects(bounds))
                yield return new CryGeometryInstance(0, asset, transform);
        }
        foreach (var doodad in instance.GetAllDoodads())
        {
            if (doodad.Template == null || ignored?.Invoke(doodad) == true)
                continue;
            var asset = geometry.LoadDoodad(doodad);
            if (asset == null)
                continue;
            var transform = HousingGeometryAssets.Transform(doodad);
            if (asset.Bounds.Transform(transform).Intersects(bounds))
                yield return new CryGeometryInstance(doodad.ObjId, asset, transform, 1, doodad.Template.NoCollision);
        }
        foreach (var other in getOtherInstances(bounds))
            yield return other;
    }

    private CryTerrainGrid GetTerrain(int x, int y) => assets.GetTerrain(world.Template, x, y);

    public float SampleHeight(float x, float y) =>
        GetTerrain(Cell(x), Cell(y))?.SampleHeight(x, y) ?? float.NaN;

    public float SampleRawHeight(int x, int y) =>
        GetTerrain(Cell(x), Cell(y))?.SampleRawHeight(x, y) ?? float.NaN;

    public float GetTerrainUnitSize(Vector3 position) =>
        GetTerrain(Cell(position.X), Cell(position.Y))?.UnitSize ?? float.NaN;

    public CryIntersection IntersectBox(CryBox box) => _scene.IntersectBox(box);

    public CryIntersection Raycast(Vector3 origin, Vector3 vector, out CrySceneRayHit hit)
    {
        hit = default;
        var length = vector.Length();
        if (!float.IsFinite(length) || length <= 0)
            return CryIntersection.Indeterminate;
        var direction = vector / length;
        var result = _scene.Raycast(origin, direction, length, out hit);
        if (result == CryIntersection.Indeterminate)
            return result;
        var end = origin + vector;
        for (var y = Cell(MathF.Min(origin.Y, end.Y)); y <= Cell(MathF.Max(origin.Y, end.Y)); y++)
        for (var x = Cell(MathF.Min(origin.X, end.X)); x <= Cell(MathF.Max(origin.X, end.X)); x++)
        {
            var terrain = GetTerrain(x, y);
            if (terrain == null)
                return CryIntersection.Indeterminate;
            var terrainResult = terrain.Raycast(origin, direction,
                result == CryIntersection.Intersects ? hit.Hit.Distance : length, out var terrainHit);
            if (terrainResult == CryIntersection.Indeterminate)
                return terrainResult;
            if (terrainResult == CryIntersection.Intersects)
            {
                hit = new CrySceneRayHit(terrainHit, 0, true);
                result = CryIntersection.Intersects;
            }
        }
        return result;
    }

    private static int Cell(float value) => checked((int)MathF.Floor(value / WorldManager.CELL_SIZE));
}
