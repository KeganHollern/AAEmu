using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Loads authored r208022 collision proxies without replacing them with render triangles.</summary>
public sealed class CryGeometryResolver(Func<string, System.IO.Stream> openFile)
{
    private readonly ConcurrentDictionary<string, CryGeometryAsset> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CryGeometryAsset Load(string modelUri) => _cache.GetOrAdd(Normalize(modelUri), LoadCore);

    private CryGeometryAsset LoadCore(string uri)
    {
        var separator = uri.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator < 0 ? "cgf" : uri[..separator];
        var path = separator < 0 ? uri : uri[(separator + 3)..];
        if (scheme == "prefab")
            return LoadPrefab(path);
        if (scheme == "entity")
            throw new NotSupportedException("Entity model geometry needs its native entity definition.");
        if (scheme is not ("cgf" or "vegetation" or "cga" or "cga_loop"))
            return Load("objects/box_nodraw.cgf");
        using var stream = OpenFile(AssetPath(path));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var asset = ReadCgf(buffer.ToArray(), AssetPath(path));
        if (scheme is "cga" or "cga_loop" || path.EndsWith(".chr", StringComparison.Ordinal))
            asset = asset with { PoseRequirements = [new CryGeometryPoseRequirement(uri, "", Matrix4x4.Identity, "", true, true)] };
        return asset;
    }

    private CryGeometryAsset LoadPrefab(string path)
    {
        var separator = path.IndexOf(".xml/", StringComparison.Ordinal);
        if (separator < 0)
            throw new InvalidDataException("Prefab model has no library and element name.");
        using var stream = OpenFile(AssetPath(path[..(separator + 4)]));
        var root = XDocument.Load(stream);
        var name = path[(separator + 5)..];
        var prefab = root.Descendants("Prefab").SingleOrDefault(x =>
            string.Equals((string)x.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException($"Missing prefab element '{name}'.");
        var parts = new List<CryGeometryPart>();
        var helpers = new List<CryGeometryHelper>();
        var poses = new List<CryGeometryPoseRequirement>();
        CryBounds? bounds = null;
        var objects = prefab.Element("Objects")?.Elements("Object") ?? [];
        var objectIndex = 0;
        foreach (var obj in objects)
        {
            objectIndex++;
            var kind = (string)obj.Attribute("Type");
            if (kind == "Comment")
            {
                helpers.Add(new CryGeometryHelper((string)obj.Attribute("Name") ?? "",
                    (string)obj.Attribute("Comment") ?? "", ReadPrefabTransform(obj)));
                continue;
            }
            var childPath = kind switch
            {
                "Brush" => (string)obj.Attribute("Prefab"),
                "GeomEntity" => (string)obj.Attribute("Geometry") ?? (string)obj.Element("Properties")?.Attribute("object_Model"),
                "Entity" => (string)obj.Element("Properties")?.Attribute("object_Model"),
                _ => null
            };
            if (kind == "Prefab" || obj.Elements("Objects").Any() || obj.Attribute("Parent") != null)
                throw new NotSupportedException("Nested prefab transforms need their native object hierarchy.");
            if (string.IsNullOrWhiteSpace(childPath))
                continue;
            var child = Load(childPath);
            var transform = ReadPrefabTransform(obj);
            var childBounds = child.Bounds.Transform(transform);
            bounds = bounds?.Union(childBounds) ?? childBounds;
            var material = (string)obj.Attribute("Material");
            var physics = obj.Element("Properties")?.Element("Physics");
            var physicalized = (string)physics?.Attribute("bPhysicalize") != "0";
            var animation = obj.Element("Properties")?.Element("Animation");
            if (animation != null && (string)animation.Attribute("bPlaying") == "1")
                poses.Add(new CryGeometryPoseRequirement(childPath, (string)obj.Attribute("Name") ?? "", transform,
                    (string)animation.Attribute("Animation") ?? "", true, physicalized));
            poses.AddRange(child.PoseRequirements.Select(pose => pose with { Transform = pose.Transform * transform }));
            helpers.AddRange(child.Helpers.Select(helper => helper with { Transform = helper.Transform * transform }));
            if (physicalized)
                parts.AddRange(child.Parts.Select(part => part with
            {
                Transform = part.Transform * transform,
                PhysicsGroup = $"{path}#{objectIndex}/{part.PhysicsGroup}",
                MaterialPath = string.IsNullOrWhiteSpace(material) ? part.MaterialPath : AssetPath(material)
            }));
        }
        return new CryGeometryAsset(bounds ?? throw new InvalidDataException("Prefab contains no supported model bounds."), parts)
        {
            Helpers = helpers,
            PoseRequirements = poses
        };
    }

    public static Matrix4x4 ReadPrefabTransform(XElement obj)
    {
        var position = ReadVector((string)obj.Attribute("Pos"), Vector3.Zero);
        var scale = ReadVector((string)obj.Attribute("Scale"), Vector3.One);
        var values = ((string)obj.Attribute("Rotate") ?? "1,0,0,0").Split(',');
        if (values.Length != 4)
            throw new InvalidDataException("Invalid prefab quaternion.");
        var rotation = new Quaternion(Parse(values[1]), Parse(values[2]), Parse(values[3]), Parse(values[0]));
        if (MathF.Abs(rotation.LengthSquared() - 1) > 0.001f || scale.X == 0 || scale.Y == 0 || scale.Z == 0)
            throw new InvalidDataException("Invalid prefab transform.");
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
    }

    public static CryGeometryAsset ReadCgf(byte[] data, string path = "")
    {
        using var reader = new BinaryReader(new MemoryStream(data, false));
        if (Encoding.ASCII.GetString(CryPhysicsDataReader.ReadExactly(reader, 6)) != "CryTek")
            throw new InvalidDataException("Invalid CGF signature.");
        reader.BaseStream.Position = 12;
        var version = reader.ReadInt32();
        if (version is not (0x744 or 0x745))
            throw new NotSupportedException($"Unsupported CGF version {version:X}.");
        var table = reader.ReadInt32();
        Seek(reader, table, 4);
        var count = CryPhysicsDataReader.ReadCount(reader, (data.Length - table - 4) / (version == 0x744 ? 16 : 20));
        var chunks = new Dictionary<int, Chunk>();
        for (var i = 0; i < count; i++)
        {
            var kind = reader.ReadUInt32();
            var chunkVersion = reader.ReadInt32();
            var offset = reader.ReadInt32();
            var id = reader.ReadInt32();
            var size = version == 0x745 ? reader.ReadInt32() : 0;
            if (offset < 20 || offset > data.Length - 16 || size < 0 || size > data.Length - offset)
                throw new InvalidDataException("Invalid CGF chunk range.");
            chunks.Add(id, new Chunk(kind, chunkVersion, offset + 16, size));
        }
        var nodes = new Dictionary<int, Node>();
        var mergeAll = false;
        var spineCount = 0;
        foreach (var chunk in chunks.Values.Where(chunk => chunk.Kind == 0xaafc0005))
        {
            RequireVersion(chunk, 1);
            Seek(reader, chunk.Body, 16);
            spineCount = CryPhysicsDataReader.ReadCount(reader, data.Length / 24);
        }
        foreach (var chunk in chunks.Values.Where(chunk => chunk.Kind == 0xcccc0015))
        {
            RequireVersion(chunk, 1);
            Seek(reader, chunk.Body, 4);
            mergeAll = (reader.ReadInt32() & 1) != 0;
        }
        foreach (var (id, chunk) in chunks.Where(x => x.Value.Kind == 0xcccc000b))
        {
            RequireVersion(chunk, 0x823);
            Seek(reader, chunk.Body, 148);
            var name = ReadName(reader, 64);
            var mesh = reader.ReadInt32();
            var parent = reader.ReadInt32();
            reader.ReadInt32();
            var material = reader.ReadInt32();
            reader.ReadInt32();
            var values = new float[16];
            for (var i = 0; i < values.Length; i++)
                values[i] = CryPhysicsDataReader.ReadFloat(reader);
            var matrix = new Matrix4x4(values[0], values[1], values[2], 0, values[4], values[5], values[6], 0,
                values[8], values[9], values[10], 0, values[12] * 0.01f, values[13] * 0.01f, values[14] * 0.01f, 1);
            nodes.Add(id, new Node(name, mesh, parent, material, matrix));
        }

        Matrix4x4 Transform(int id, HashSet<int> chain)
        {
            if (!chain.Add(id) || !nodes.TryGetValue(id, out var node))
                throw new InvalidDataException("Invalid CGF node hierarchy.");
            return node.Parent == -1 ? node.Transform : node.Transform * Transform(node.Parent, chain);
        }

        var modelNodes = nodes.Where(pair => chunks.TryGetValue(pair.Value.Mesh, out var chunk) &&
            chunk.Kind == 0xcccc0000 && !pair.Value.Name.StartsWith('$') &&
            !pair.Value.Name.Contains("PhysicsProxy", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
        var merged = mergeAll || (modelNodes.Length <= 1 && !nodes.Values.Any(node =>
            node.Name.StartsWith("$joint", StringComparison.Ordinal) || node.Name.StartsWith("$cutdown", StringComparison.Ordinal)));
        CryBounds? bounds = null;
        var parts = new List<CryGeometryPart>();
        foreach (var (id, node) in nodes)
        {
            if (!chunks.TryGetValue(node.Mesh, out var mesh) || mesh.Kind != 0xcccc0000)
                continue;
            RequireVersion(mesh, 0x800);
            var transform = merged && modelNodes.Contains(id) ? Matrix4x4.Identity : Transform(id, []);
            Seek(reader, mesh.Body + 8, 124);
            var vertexCount = CryPhysicsDataReader.ReadCount(reader, 65536);
            var indexCount = CryPhysicsDataReader.ReadCount(reader, data.Length / 2);
            reader.ReadInt32();
            var subsetId = reader.ReadInt32();
            reader.ReadInt32();
            var streams = new int[16];
            for (var i = 0; i < streams.Length; i++)
                streams[i] = reader.ReadInt32();
            var physics = new int[4];
            for (var i = 0; i < physics.Length; i++)
                physics[i] = reader.ReadInt32();
            var localBounds = new CryBounds(CryPhysicsDataReader.ReadVector(reader), CryPhysicsDataReader.ReadVector(reader));
            if (localBounds.Min.X > localBounds.Max.X || localBounds.Min.Y > localBounds.Max.Y || localBounds.Min.Z > localBounds.Max.Z)
                throw new InvalidDataException("Invalid CGF mesh bounds.");
            // Native LoadFromContentCGF includes empty proxy meshes when they are ordinary mesh nodes.
            if (modelNodes.Contains(id) && (!merged || id == modelNodes[0]))
            {
                var transformedBounds = localBounds.Transform(transform);
                bounds = bounds?.Union(transformedBounds) ?? transformedBounds;
            }
            Vector3[] vertices = null;
            ushort[] indices = null;
            byte[] materials = null;
            if (vertexCount > 0 && streams[0] > 0)
            {
                ReadStream(reader, chunks[streams[0]], 0, vertexCount, 12);
                vertices = new Vector3[vertexCount];
                for (var i = 0; i < vertices.Length; i++)
                    vertices[i] = CryPhysicsDataReader.ReadVector(reader);
            }
            if (indexCount > 0 && streams[5] > 0)
            {
                ReadStream(reader, chunks[streams[5]], 5, indexCount, 2);
                indices = new ushort[indexCount];
                for (var i = 0; i < indices.Length; i++)
                    indices[i] = reader.ReadUInt16();
                materials = ReadMaterials(reader, chunks, subsetId, indexCount);
            }
            var materialPath = "";
            if (chunks.TryGetValue(node.Material, out var materialChunk) && materialChunk.Kind == 0xcccc0014)
            {
                RequireVersion(materialChunk, 0x800);
                Seek(reader, materialChunk.Body + 8, 128);
                var name = ReadName(reader, 128);
                if (name.Length > 0)
                    materialPath = name.Contains('/') || name.Contains('\\') ? AssetPath(name) :
                        path[..(path.LastIndexOf('/') + 1)] + name.ToLowerInvariant();
            }
            for (var slot = 0; slot < physics.Length; slot++)
            {
                if (merged && modelNodes.Contains(id) && id != modelNodes[0])
                    continue;
                if (physics[slot] <= 0)
                    continue;
                var chunk = chunks[physics[slot]];
                if (chunk.Kind != 0xcccc0018)
                    throw new InvalidDataException("Invalid CGF physics chunk reference.");
                RequireVersion(chunk, 0x800);
                Seek(reader, chunk.Body, 24);
                var size = reader.ReadInt32();
                Seek(reader, chunk.Body + 24, size);
                var shape = CryPhysicsDataReader.Read(CryPhysicsDataReader.ReadExactly(reader, size), vertices, indices, materials);
                parts.Add(new CryGeometryPart(shape, transform, 0x1000 + slot, materialPath, node.Name)
                {
                    PhysicsGroup = $"{path}#{(merged ? 0 : id)}",
                    SpineCount = merged ? spineCount : 0,
                    PickingIndex = GetPickingIndex(node.Name)
                });
            }
        }
        if (bounds == null)
            throw new InvalidDataException("CGF contains no supported model bounds.");
        return new CryGeometryAsset(bounds.Value, parts);
    }

    private static byte[] ReadMaterials(BinaryReader reader, Dictionary<int, Chunk> chunks, int id, int indexCount)
    {
        if (id <= 0)
            return null;
        var chunk = chunks[id];
        RequireVersion(chunk, 0x800);
        Seek(reader, chunk.Body, 16);
        reader.ReadInt32();
        var count = CryPhysicsDataReader.ReadCount(reader, indexCount);
        reader.ReadInt64();
        var materials = new byte[indexCount / 3];
        for (var i = 0; i < count; i++)
        {
            var first = reader.ReadInt32();
            var length = reader.ReadInt32();
            reader.ReadInt64();
            var material = reader.ReadInt32();
            CryPhysicsDataReader.ReadExactly(reader, 16);
            if (first < 0 || length < 0 || first > indexCount - length || first % 3 != 0 || length % 3 != 0)
                throw new InvalidDataException("Invalid CGF mesh subset.");
            Array.Fill(materials, unchecked((byte)material), first / 3, length / 3);
        }
        return materials;
    }

    private static void ReadStream(BinaryReader reader, Chunk chunk, int kind, int count, int size)
    {
        RequireVersion(chunk, 0x800);
        Seek(reader, chunk.Body, 24);
        reader.ReadInt32();
        if (reader.ReadInt32() != kind || reader.ReadInt32() != count || reader.ReadInt32() != size)
            throw new NotSupportedException("Unsupported CGF vertex or index stream.");
        reader.ReadInt64();
    }

    private static string ReadName(BinaryReader reader, int size) => Encoding.UTF8.GetString(
        CryPhysicsDataReader.ReadExactly(reader, size)).Split('\0')[0];

    private static void RequireVersion(Chunk chunk, int expected)
    {
        if (chunk.Version != expected)
            throw new NotSupportedException($"Unsupported CGF chunk {chunk.Kind:X} version {chunk.Version:X}.");
    }

    private static void Seek(BinaryReader reader, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > reader.BaseStream.Length - length)
            throw new InvalidDataException("Invalid CGF data range.");
        reader.BaseStream.Position = offset;
    }

    private static Vector3 ReadVector(string text, Vector3 fallback)
    {
        if (text == null)
            return fallback;
        var parts = text.Split(',');
        if (parts.Length != 3)
            throw new InvalidDataException("Invalid prefab vector.");
        return new Vector3(Parse(parts[0]), Parse(parts[1]), Parse(parts[2]));
    }

    private static float Parse(string text)
    {
        var value = float.Parse(text, CultureInfo.InvariantCulture);
        if (!float.IsFinite(value))
            throw new InvalidDataException("Non-finite prefab coordinate.");
        return value;
    }

    public static string Normalize(string path) => path.Replace('\\', '/').Trim().ToLowerInvariant();

    private System.IO.Stream OpenFile(string path) => openFile(path) ??
        throw new FileNotFoundException($"Missing client geometry asset '{path}'.", path);

    private static int GetPickingIndex(string name)
    {
        if (!name.StartsWith("$picking", StringComparison.OrdinalIgnoreCase))
            return 0;
        var text = name.AsSpan(8).TrimStart();
        var count = 0;
        if (text.Length > 0 && text[0] is '+' or '-')
            count++;
        while (count < text.Length && char.IsAsciiDigit(text[count]))
            count++;
        return int.TryParse(text[..count], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : 0;
    }
    private static string AssetPath(string path) => Normalize(path).StartsWith("game/", StringComparison.Ordinal)
        ? Normalize(path) : "game/" + Normalize(path);

    private sealed record Chunk(uint Kind, int Version, int Body, int Size);
    private sealed record Node(string Name, int Mesh, int Parent, int Material, Matrix4x4 Transform);
}
