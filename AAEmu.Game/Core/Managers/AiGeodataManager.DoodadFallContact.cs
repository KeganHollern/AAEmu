using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    private sealed record DoodadFloor(uint Phase, Vector3 Rotation, float Scale, LevelFace[] Faces, long RetryAfter, bool StockModel);
    private readonly ConditionalWeakTable<Doodad, DoodadFloor> _doodadFloors = new();

    internal bool HasStaticDoodadFallSupport(Unit actor, Vector3 foot)
    {
        // Search only close to a static spawn. This cost bound does not expand any collision face.
        const float radius = 2;
        var world = actor.ParentWorld;
        if (actor.Region == null || world?.Regions is not { } regions ||
            !float.IsFinite(foot.X) || !float.IsFinite(foot.Y) || !float.IsFinite(foot.Z))
            return false;
        var minX = Math.Max(0, (int)MathF.Floor((foot.X - radius) / WorldManager.REGION_SIZE));
        var maxX = Math.Min(regions.GetLength(0) - 1, (int)MathF.Floor((foot.X + radius) / WorldManager.REGION_SIZE));
        var minY = Math.Max(0, (int)MathF.Floor((foot.Y - radius) / WorldManager.REGION_SIZE));
        var maxY = Math.Min(regions.GetLength(1) - 1, (int)MathF.Floor((foot.Y + radius) / WorldManager.REGION_SIZE));
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        {
            if (regions[x, y] is not { } region)
                continue;
            foreach (var entry in region.DoodadOriginsFrom(foot.X - radius))
            {
                if (entry.Position.X > foot.X + radius)
                    break;
                if (Vector3.DistanceSquared(entry.Position, foot) > radius * radius)
                    continue;
                var doodad = entry.Doodad;
                if (ReferenceEquals(doodad.ParentWorld, world) && doodad.IsVisible &&
                    Region.IsStaticDoodadCandidate(doodad) &&
                    doodad.Transform.World.Position == entry.Position &&
                    HasDoodadFace(doodad, foot - entry.Position))
                    return true;
            }
        }
        foreach (var entry in world.DoodadFloors.At(foot))
        {
            if (!entry.Bounds.Contains(foot) || Vector3.DistanceSquared(entry.Origin, foot) <= radius * radius)
                continue;
            var doodad = entry.Doodad;
            if (ReferenceEquals(doodad.ParentWorld, world) && doodad.IsVisible && Region.IsStaticDoodadCandidate(doodad) &&
                doodad.Transform.World.Position == entry.Origin && HasDoodadFace(doodad, foot - entry.Origin, stockOnly: true))
                return true;
        }
        return false;
    }

    private bool HasDoodadFace(Doodad doodad, Vector3 relativeFoot, bool stockOnly = false)
    {
        var phase = doodad.FuncGroupId;
        var rotation = doodad.Transform.World.Rotation;
        if (!_doodadFloors.TryGetValue(doodad, out var floor) || floor.Phase != phase ||
            floor.Rotation != rotation || floor.Scale != doodad.Scale ||
            (floor.RetryAfter != 0 && Environment.TickCount64 >= floor.RetryAfter))
        {
            var faces = ReadDoodadFaces(doodad, phase, out var retry, out var stockModel);
            floor = new DoodadFloor(phase, rotation, doodad.Scale, faces, retry, stockModel);
            _doodadFloors.AddOrUpdate(doodad, floor);
        }
        return (!stockOnly || floor.StockModel) && TouchesLevelFace(floor.Faces, relativeFoot);
    }

    private static LevelFace[] ReadDoodadFaces(Doodad doodad, uint phase, out long retry, out bool stockModel)
    {
        retry = 0;
        stockModel = false;
        if (phase == 0 || !doodad.Template.FuncGroups.Any(group => group.Id == phase &&
                group.GroupKindId != DoodadFuncGroups.DoodadFuncGroupKind.End) ||
            DoodadManager.Instance.GetPhaseFunc(phase).Any(func => func.FuncType != nameof(DoodadFuncTimer)))
            return [];
        var path = HousingGeometryAssets.DoodadModelPath(doodad.Template, phase);
        if (path?.StartsWith("cgf://", StringComparison.OrdinalIgnoreCase) != true ||
            !path.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
            return [];
        try
        {
            if (ClientFileManager.FileExists(CryGeometryResolver.AssetPath(path[6..])))
            {
                var asset = HousingManager.Instance.GeometryAssets.Load(path);
                if (asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
                    return [];
                stockModel = asset.SourceSha256 != null && StockDoodadFloorModel.Find(path)?.Sha256 == asset.SourceSha256;
                var placement = HousingGeometryAssets.Transform(doodad);
                var faces = new List<LevelFace>(ReadLevelFaces(placement, asset));
                placement.Translation = Vector3.Zero;
                foreach (var part in asset.Parts)
                {
                    if (part is { PhysicsType: 0x1000, Shape: CryBox box })
                        AddLevelBoxFace(faces, box, part.Transform * placement);
                }
                return faces.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            Logger.Debug(ex, "Could not read the static doodad model {0}", path);
        }
        retry = Environment.TickCount64 + 30_000;
        return [];
    }

    // These are the extents of the physical box in its own axes, including its authored orientation.
    private static void AddLevelBoxFace(List<LevelFace> faces, CryBox box, Matrix4x4 transform)
    {
        var orientation = box.Orientation * transform;
        var x = Vector3.TransformNormal(Vector3.UnitX * box.HalfSize.X, orientation);
        var y = Vector3.TransformNormal(Vector3.UnitY * box.HalfSize.Y, orientation);
        var z = Vector3.TransformNormal(Vector3.UnitZ * box.HalfSize.Z, orientation);
        if (x.Z != 0)
            (x, z) = (z, x);
        else if (y.Z != 0)
            (y, z) = (z, y);
        if (x.Z != 0 || y.Z != 0 || z.Z == 0)
            return;
        var area = Vector3.Cross(x, y).Z;
        if (area == 0)
            return;
        if (area < 0)
            (x, y) = (y, x);
        var top = Vector3.Transform(box.Center, transform) + (z.Z > 0 ? z : -z);
        faces.Add(new LevelFace(top - x - y, top + x - y, top + x + y));
        faces.Add(new LevelFace(top - x - y, top + x + y, top - x + y));
    }
}
