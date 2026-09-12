using System.Collections.Concurrent;
using System.Numerics;
using System.Xml.Linq;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<CryPrefabWaterVolume>> _water = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads water metadata without loading meshes or animation poses. XML order is registration order.</summary>
    public IReadOnlyList<CryPrefabWaterVolume> LoadWater(string modelUri) =>
        string.IsNullOrWhiteSpace(modelUri) ? [] : _water.GetOrAdd(Normalize(modelUri), LoadWaterCore);

    private IReadOnlyList<CryPrefabWaterVolume> LoadWaterCore(string uri)
    {
        if (!uri.StartsWith("prefab://", StringComparison.Ordinal))
            return [];
        var path = uri[9..];
        var separator = path.IndexOf(".xml/", StringComparison.Ordinal);
        if (separator < 0)
            throw new InvalidDataException("Prefab model has no library and element name.");
        var root = _prefabLibraries.GetOrAdd(AssetPath(path[..(separator + 4)]), ReadPrefabLibrary);
        var name = path[(separator + 5)..];
        var prefab = root.Descendants("Prefab").SingleOrDefault(element =>
            string.Equals((string)element.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        if (prefab == null)
            return [];
        var origin = ReadPrefabOrigin(prefab);
        var result = new List<CryPrefabWaterVolume>();
        foreach (var obj in prefab.Element("Objects")?.Elements("Object") ?? [])
        {
            var kind = (string)obj.Attribute("Type");
            if (kind == "Prefab" || obj.Elements("Objects").Any() || obj.Attribute("Parent") != null)
                throw new NotSupportedException("Nested prefab transforms need their native object hierarchy.");
            if (kind == "WaterVolume")
            {
                var water = ReadWaterVolume(obj, origin);
                if (water != null)
                    result.Add(water);
                continue;
            }
            var childPath = kind switch
            {
                "Brush" => (string)obj.Attribute("Prefab"),
                "GeomEntity" => (string)obj.Attribute("Geometry") ?? (string)obj.Element("Properties")?.Attribute("object_Model"),
                "Entity" => (string)obj.Element("Properties")?.Attribute("object_Model"),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(childPath))
                continue;
            var childWater = LoadWater(childPath);
            if (childWater.Count == 0)
                continue;
            var transform = ReadPrefabTransform(obj, origin);
            result.AddRange(childWater.Select(water => water with { Transform = water.Transform * transform }));
        }
        return result;
    }

    private static Vector3 ReadPrefabOrigin(XElement prefab)
    {
        // Native390ff370 replaces the origin for each matching comment. Post-process39110b40
        // subtracts the final value from every loaded child, including children before the comment.
        var origin = prefab.Element("Objects")?.Elements("Object").LastOrDefault(obj =>
            (string)obj.Attribute("Type") == "Comment" && (string)obj.Attribute("Name") == "origin");
        return ReadVector((string)origin?.Attribute("Pos"), Vector3.Zero);
    }

    private static Matrix4x4 ReadPrefabTransform(XElement obj, Vector3 origin) =>
        ReadPrefabTransform(obj) * Matrix4x4.CreateTranslation(-origin);

    private static CryPrefabWaterVolume ReadWaterVolume(XElement obj, Vector3 origin)
    {
        var points = obj.Element("Points")?.Elements("Point")
            .Select(point => ReadVector((string)point.Attribute("Pos"), Vector3.Zero)).ToArray() ?? [];
        // WaterVolumePrefabObject::Load3910cd10 does not create its render node below four points.
        if (points.Length < 4)
            return null;
        return new CryPrefabWaterVolume((string)obj.Attribute("Name") ?? "", points, ReadPrefabTransform(obj, origin),
            Parse((string)obj.Attribute("VolumeDepth") ?? "0"));
    }
}
