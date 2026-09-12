using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>Authored physics water used only by housing placement, without movement ingest filters.</summary>
public sealed class HousingWaterGeometry
{
    private readonly List<Volume> _volumes = [];
    private readonly HashSet<ulong> _registeredIds = [];

    public int Count => _volumes.Count;

    /// <summary>Registers records in native load order. A repeated VolumeId cannot add another physics contour.</summary>
    public void Add(ObjectDataType11Water water, Vector3 cellOffset)
    {
        if (water.VolumeType is not (WaterObjectVolumeType.Area or WaterObjectVolumeType.River) ||
            _registeredIds.Contains(water.VolumeId))
            return;
        // Native301f6ab0 registers every area node, but only river nodes with a physics contour.
        if (water.VolumeType == WaterObjectVolumeType.Area || water.PhysicsContourPointsList.Count != 0)
            _registeredIds.Add(water.VolumeId);
        var count = water.PhysicsContourPointsList.Count;
        if (count < 4 || water.VolumeType == WaterObjectVolumeType.River && (count & 1) != 0)
            return;
        var normal = water.FogPlaneNormal;
        if (!Finite(normal) || !float.IsFinite(water.Depth) || !Finite(cellOffset))
            throw new InvalidDataException("Invalid housing water plane or depth.");
        if (normal.Z <= 0.0001f || water.ShapePointsList.Count < 3)
            return;
        var d = water.FogPlaneD;
        if (water.VolumeType == WaterObjectVolumeType.Area)
        {
            // Native301f6ab0 replaces the area's plane distance with the first render vertex height.
            d = -water.ShapePointsList[0].Z;
        }
        else
        {
            // The river loader first anchors the fog plane at the render AABB maximum, then
            // moves the highest projected render vertex to EndPos.Z before projecting physics vertices.
            d = -Vector3.Dot(normal, water.EndPos);
            var highest = 0f;
            foreach (var point in water.ShapePointsList)
                highest = MathF.Max(highest, Project(point, normal, d).Z);
            if (highest != water.EndPos.Z)
                d -= normal.Z * (water.EndPos.Z - highest);
        }
        if (!float.IsFinite(d))
            throw new InvalidDataException("Invalid housing water plane distance.");
        var points = water.PhysicsContourPointsList.Select(point => Project(point, normal, d) + cellOffset).ToArray();
        if (points.Any(point => !Finite(point)))
            throw new InvalidDataException("Invalid housing water contour.");
        // Both native contour setters orient vertices counter-clockwise before CreateArea.
        var area = 0d;
        for (var i = 0; i < points.Length; i++)
        {
            var next = points[(i + 1) % points.Length];
            area += (double)points[i].X * next.Y - (double)next.X * points[i].Y;
        }
        if (area == 0)
            return;
        if (area < 0)
            Array.Reverse(points);
        normal = Vector3.Normalize(normal);
        _volumes.Add(new Volume(points, normal, MathF.Min(0, -water.Depth),
            water.VolumeType == WaterObjectVolumeType.River));
    }

    /// <summary>Native3012b2e0 returns the greater of ocean and the selected physics-plane projection.</summary>
    public float GetWaterLevel(Vector3 point, float oceanLevel)
        => GetWaterLevel(point, oceanLevel, []);

    /// <summary>
    /// Adds active prefab water after static world registration, in caller-supplied oldest-to-newest order.
    /// Both sources share the native four-result limit. Dynamic records never change the static cache.
    /// </summary>
    public float GetWaterLevel(Vector3 point, float oceanLevel, IReadOnlyList<CryWaterVolumeInstance> dynamicInstances)
    {
        if (!Finite(point) || !float.IsFinite(oceanLevel))
            return float.NaN;
        // Native35053450 prepends same-medium areas. The first local match replaces global
        // buoyancy and clears its medium marker. Later matches append, up to 4 result slots.
        var level = oceanLevel;
        var count = 0;
        for (var instanceIndex = dynamicInstances.Count - 1; instanceIndex >= 0 && count < 4; instanceIndex--)
        {
            var instance = dynamicInstances[instanceIndex];
            for (var child = instance.Volumes.Count - 1; child >= 0 && count < 4; child--)
            {
                var water = instance.Volumes[child];
                var points = water.GetPhysicsContour(instance.Transform, instance.HeightOffset, out var normal);
                if (points.Length == 0)
                    continue;
                // Area setters orient the projected contour counter-clockwise before CreateArea.
                var area = 0d;
                for (var i = 0; i < points.Length; i++)
                {
                    var next = points[(i + 1) % points.Length];
                    area += (double)points[i].X * next.Y - (double)next.X * points[i].Y;
                }
                if (area == 0)
                    continue;
                if (area < 0)
                    Array.Reverse(points);
                var volume = new Volume(points, normal, MathF.Min(0, -water.Depth - instance.HeightOffset), false);
                if (volume.TryGetSurface(point, out var surface))
                {
                    level = MathF.Max(level, surface);
                    count++;
                }
            }
        }
        for (var i = _volumes.Count - 1; i >= 0 && count < 4; i--)
            if (_volumes[i].TryGetSurface(point, out var surface))
            {
                level = MathF.Max(level, surface);
                count++;
            }
        return level;
    }

    public static HousingWaterGeometry Load(WorldTemplate world, Func<string, System.IO.Stream> openFile)
    {
        var result = new HousingWaterGeometry();
        for (var y = 0; y < world.Cells.GetLength(1); y++)
        for (var x = 0; x < world.Cells.GetLength(0); x++)
        {
            var prefix = $"game/worlds/{world.Name}/cells/{x:000}_{y:000}/client/";
            var offset = new Vector3(x * WorldManager.CELL_SIZE, y * WorldManager.CELL_SIZE, 0);
            var objects = world.Cells[x, y]?.LoadedObjectDat;
            if (objects == null)
            {
                using var stream = openFile(prefix + "object.dat");
                if (stream != null)
                {
                    objects = new ObjectsFile(prefix + "object.dat");
                    if (!objects.ReadFile(stream) || objects.HasUnparsedObjects)
                        throw new InvalidDataException($"Cannot read housing water: {prefix}object.dat.");
                }
            }
            if (objects != null)
                foreach (var water in objects.PrefabsList.OfType<ObjectDataType11Water>())
                    result.Add(water, offset);
            // Native301491a0 reads object.dat before big_object.dat for each cell.
            using var bigStream = openFile(prefix + "big_object.dat");
            if (bigStream != null)
                foreach (var water in CryBigObjectsFile.Read(bigStream).Objects.OfType<ObjectDataType11Water>())
                    result.Add(water, offset);
        }
        return result;
    }

    private static Vector3 Project(Vector3 point, Vector3 normal, float d) =>
        point with { Z = point.Z - (Vector3.Dot(normal, point) + d) / normal.Z };

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class Volume(Vector3[] points, Vector3 normal, float bottom, bool river)
    {
        public bool TryGetSurface(Vector3 point, out float surface)
        {
            var distance = Vector3.Dot(normal, point - points[0]);
            surface = point.Z - distance * normal.Z;
            // CreateArea35054890 uses its fitted plane basis, with strict depth limits.
            // Planar areas add .01 to the +10 upper extent. River triangle meshes also test the surface.
            if (!(distance > bottom && distance < (river ? 10 : 10.01f)))
                return false;
            if (river && distance > 0)
                return false;
            var projected = point - normal * distance;
            var minX = points.Min(vertex => vertex.X);
            var maxX = points.Max(vertex => vertex.X);
            var minY = points.Min(vertex => vertex.Y);
            var maxY = points.Max(vertex => vertex.Y);
            if (!(projected.X > minX && projected.X < maxX && projected.Y > minY && projected.Y < maxY))
                return false;
            var closest = float.PositiveInfinity;
            var inside = false;
            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                if (projected.X < MathF.Min(a.X, b.X) || projected.X >= MathF.Max(a.X, b.X))
                    continue;
                var dx = b.X - a.X;
                var crossing = a.Y + (projected.X - a.X) * (b.Y - a.Y) / dx;
                if (crossing > projected.Y && crossing < closest)
                {
                    closest = crossing;
                    inside = dx < 0;
                }
            }
            return inside;
        }
    }
}
