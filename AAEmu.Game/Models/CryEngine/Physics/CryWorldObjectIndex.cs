using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed record CryWorldObjectInstance(string ModelUri, Matrix4x4 Transform,
    Vector3 Min, Vector3 Max, string Source);

/// <summary>
/// Immutable broad phase for authored brush instances. Model geometry loads only
/// after a query selects an instance. The index is specific to one world template.
/// </summary>
public sealed class CryWorldObjectIndex
{
    private const int BucketSize = 64;
    private readonly CryWorldObjectInstance[] _instances;
    private readonly Dictionary<(int X, int Y), List<int>> _buckets = [];

    public int Count => _instances.Length;

    public CryWorldObjectIndex(IEnumerable<CryWorldObjectInstance> instances)
    {
        _instances = instances.ToArray();
        for (var i = 0; i < _instances.Length; i++)
        {
            var instance = _instances[i];
            CheckBounds(instance.Min, instance.Max);
            for (var y = Bucket(instance.Min.Y); y <= Bucket(instance.Max.Y); y++)
            for (var x = Bucket(instance.Min.X); x <= Bucket(instance.Max.X); x++)
            {
                if (!_buckets.TryGetValue((x, y), out var bucket))
                    _buckets.Add((x, y), bucket = []);
                bucket.Add(i);
            }
        }
    }

    public IReadOnlyList<CryWorldObjectInstance> Query(Vector3 min, Vector3 max)
    {
        CheckBounds(min, max);
        var matches = new SortedSet<int>();
        for (var y = Bucket(min.Y); y <= Bucket(max.Y); y++)
        for (var x = Bucket(min.X); x <= Bucket(max.X); x++)
        {
            if (!_buckets.TryGetValue((x, y), out var bucket))
                continue;
            foreach (var index in bucket)
            {
                var instance = _instances[index];
                if (instance.Min.X <= max.X && instance.Max.X >= min.X &&
                    instance.Min.Y <= max.Y && instance.Max.Y >= min.Y &&
                    instance.Min.Z <= max.Z && instance.Max.Z >= min.Z)
                    matches.Add(index);
            }
        }
        return matches.Select(index => _instances[index]).ToArray();
    }

    public static CryWorldObjectIndex Load(WorldTemplate world) => Load(world, path =>
        ClientFileManager.FileExists(path) ? ClientFileManager.GetFileStream(path) : null);

    public static CryWorldObjectIndex Load(WorldTemplate world, Func<string, System.IO.Stream> openFile)
    {
        var instances = new List<CryWorldObjectInstance>();
        for (var y = 0; y < world.Cells.GetLength(1); y++)
        for (var x = 0; x < world.Cells.GetLength(0); x++)
        {
            var path = $"game/worlds/{world.Name}/cells/{x:000}_{y:000}/client/object.dat";
            using var stream = openFile(path);
            if (stream == null)
                continue;
            var objects = new ObjectsFile(path);
            if (!objects.ReadFile(stream) || objects.HasUnparsedObjects)
                throw new InvalidDataException($"Cannot read world collision objects: {path}.");
            instances.AddRange(ReadBrushInstances(objects, x, y));
        }
        return new CryWorldObjectIndex(instances);
    }

    public static IReadOnlyList<CryWorldObjectInstance> ReadBrushInstances(ObjectsFile objects, int cellX, int cellY)
    {
        var result = new List<CryWorldObjectInstance>();
        var offset = new Vector3(cellX * WorldManager.CELL_SIZE, cellY * WorldManager.CELL_SIZE, 0);
        for (var index = 0; index < objects.PrefabsList.Count; index++)
        {
            if (objects.PrefabsList[index] is not ObjectDataType1Brush brush)
                continue;
            if (brush.PathId < 0 || brush.PathId >= objects.AssetPathsList.Count)
                throw new InvalidDataException($"Invalid brush asset {brush.PathId} in {objects.FileName}.");
            var uri = objects.AssetPathsList[brush.PathId].Name;
            if (string.IsNullOrWhiteSpace(uri))
                throw new InvalidDataException($"Empty brush asset in {objects.FileName}.");
            var matrix = brush.Matrix3X4;
            // CryEngine uses column vectors. System.Numerics uses row vectors.
            // object.dat transforms and bounds are cell-local, in game XYZ axes.
            var transform = new Matrix4x4(
                matrix.M11, matrix.M21, matrix.M31, 0,
                matrix.M12, matrix.M22, matrix.M32, 0,
                matrix.M13, matrix.M23, matrix.M33, 0,
                matrix.M14 + offset.X, matrix.M24 + offset.Y, matrix.M34, 1);
            var min = Vector3.Min(brush.StartPos, brush.EndPos) + offset;
            var max = Vector3.Max(brush.StartPos, brush.EndPos) + offset;
            CheckBounds(min, max);
            result.Add(new CryWorldObjectInstance(uri.Replace('\\', '/'), transform, min, max,
                $"{objects.FileName}#{index}"));
        }
        return result;
    }

    private static int Bucket(float value) => checked((int)MathF.Floor(value / BucketSize));

    private static void CheckBounds(Vector3 min, Vector3 max)
    {
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidDataException("Invalid world collision bounds.");
    }
}
