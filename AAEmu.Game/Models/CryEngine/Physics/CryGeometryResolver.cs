using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Loads authored r208022 collision proxies without replacing them with render triangles.</summary>
public sealed partial class CryGeometryResolver(Func<string, System.IO.Stream> openFile)
{
    private readonly ConcurrentDictionary<string, CryGeometryAsset> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CryCharacterAnimation> _animations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Model, string Name), string> _animationPaths = new();
    private readonly ConcurrentDictionary<string, XDocument> _prefabLibraries = new(StringComparer.OrdinalIgnoreCase);

    public CryGeometryAsset Load(string modelUri) => _cache.GetOrAdd(Normalize(modelUri), LoadCore);

    public CryGeometryAsset LoadPose(string modelUri, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        var uri = Normalize(modelUri);
        if (uri.StartsWith("prefab://", StringComparison.Ordinal))
            return LoadPrefab(uri[9..], elapsedSeconds);
        var asset = Load(uri);
        if (asset.CgaAnimation != null && asset.PoseRequirements.Any(pose => pose.Playing))
            return asset.CgaAnimation.Sample(asset, elapsedSeconds, uri.StartsWith("cga_loop://", StringComparison.Ordinal));
        if (asset.PoseRequirements.Any(pose => pose.Playing))
            return LoadCharacterPose(uri, "Default", elapsedSeconds, uri.StartsWith("cga_loop://", StringComparison.Ordinal));
        return asset;
    }

    public CryGeometryAsset LoadCharacterPose(string modelUri, string animationName, double elapsedSeconds, bool loop)
    {
        var asset = Load(modelUri);
        if (!asset.HasModelBounds)
            return asset;
        var uri = Normalize(modelUri);
        var separator = uri.IndexOf("://", StringComparison.Ordinal);
        var path = separator < 0 ? uri : uri[(separator + 3)..];
        var animationPath = FindCharacterAnimation(ResolveCharacterModelPath(path), animationName) ??
            throw new NotSupportedException($"Character animation '{animationName}' is not in the model CAL file.");
        var animation = _animations.GetOrAdd(animationPath, file =>
        {
            using var stream = OpenFile(file);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return CryCharacterAnimation.Read(buffer.ToArray());
        });
        return animation.Sample(asset, elapsedSeconds, loop);
    }

    private CryGeometryAsset LoadCore(string uri)
    {
        var separator = uri.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator < 0 ? "cgf" : uri[..separator];
        var path = separator < 0 ? uri : uri[(separator + 3)..];
        if (scheme == "prefab")
            return LoadPrefab(path);
        if (scheme == "entity")
            throw new NotSupportedException("Entity model geometry needs its native entity definition.");
        if ((scheme is "cga" or "cga_loop") && !IsCharacterModelPath(path))
            return EmptyModel();
        if (scheme is not ("cgf" or "vegetation" or "cga" or "cga_loop"))
            return Load("objects/box_nodraw.cgf");
        using var stream = OpenOptionalModelFile(AssetPath(path));
        if (stream == null)
            return ResolveMissingModel(scheme, AssetPath(path));
        CryGeometryAsset asset;
        if (path.EndsWith(".cdf", StringComparison.Ordinal))
            asset = LoadCharacterDefinition(path);
        else
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            asset = ReadCgf(buffer.ToArray(), AssetPath(path));
            if (asset.CharacterBones.Count > 0)
                asset = LoadCharacterLodBounds(path, asset);
        }
        var startsAnimation = scheme is "cga" or "cga_loop";
        if (startsAnimation && asset.CgaAnimation != null)
            asset = asset with { Bounds = asset.CgaAnimation.GetBindBounds() };
        var characterPath = ResolveCharacterModelPath(path);
        if (characterPath.EndsWith(".chr", StringComparison.Ordinal))
            startsAnimation &= FindCharacterAnimation(characterPath, "Default") != null;
        if (startsAnimation)
            asset = asset with { PoseRequirements = [new CryGeometryPoseRequirement(uri, "", Matrix4x4.Identity, "", true, true)
                { AffectsCollision = asset.HasAnimatedCollision }] };
        return asset;
    }

    private CryGeometryAsset LoadPrefab(string path, double? elapsedSeconds = null)
    {
        var separator = path.IndexOf(".xml/", StringComparison.Ordinal);
        if (separator < 0)
            throw new InvalidDataException("Prefab model has no library and element name.");
        var root = _prefabLibraries.GetOrAdd(AssetPath(path[..(separator + 4)]), ReadPrefabLibrary);
        var name = path[(separator + 5)..];
        var prefab = root.Descendants("Prefab").SingleOrDefault(x =>
            string.Equals((string)x.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        if (prefab == null)
            return EmptyModel();
        var origin = ReadPrefabOrigin(prefab);
        var parts = new List<CryGeometryPart>();
        var helpers = new List<CryGeometryHelper>();
        var poses = new List<CryGeometryPoseRequirement>();
        var waterVolumes = LoadWater("prefab://" + path);
        var animatedCollision = false;
        CryBounds? bounds = null;
        var objects = prefab.Element("Objects")?.Elements("Object") ?? [];
        var objectIndex = 0;
        foreach (var obj in objects)
        {
            objectIndex++;
            var kind = (string)obj.Attribute("Type");
            if (kind == "WaterVolume")
            {
                var waterBounds = ReadWaterVolume(obj, origin)?.GetModelBounds(Matrix4x4.Identity);
                if (waterBounds.HasValue)
                    bounds = bounds?.Union(waterBounds.Value) ?? waterBounds;
                continue;
            }
            if (kind == "Comment")
            {
                if ((string)obj.Attribute("Name") == "origin")
                    continue;
                helpers.Add(new CryGeometryHelper((string)obj.Attribute("Name") ?? "",
                    (string)obj.Attribute("Comment") ?? "", ReadPrefabTransform(obj, origin)));
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
            var animation = obj.Element("Properties")?.Element("Animation");
            var active = animation != null && (string)animation.Attribute("bPlaying") == "1";
            var animatedEntity = kind == "Entity" && (string)obj.Attribute("EntityClass") == "AnimObject";
            var child = animatedEntity && !IsCharacterModelPath(childPath) ? EmptyModel() : Load(childPath);
            if (animatedEntity && child.CgaAnimation != null)
                child = child with { Bounds = child.CgaAnimation.GetBindBounds() };
            if (active && ResolveCharacterModelPath(Normalize(childPath)).EndsWith(".chr", StringComparison.Ordinal) &&
                FindCharacterAnimation(ResolveCharacterModelPath(Normalize(childPath)), (string)animation.Attribute("Animation") ?? "Default") == null)
                active = false;
            if (elapsedSeconds.HasValue && active)
            {
                var time = elapsedSeconds.Value * Parse((string)animation.Attribute("Speed") ?? "1");
                var loop = (string)animation.Attribute("bLoop") == "1";
                if (child.CgaAnimation != null)
                    child = LoadCgaPose(childPath, (string)animation.Attribute("Animation") ?? "Default", time, loop);
                else if (child.CharacterBones.Count > 0)
                    child = LoadCharacterPose(childPath, (string)animation.Attribute("Animation") ?? "Default", time, loop);
            }
            var transform = ReadPrefabTransform(obj, origin);
            if (child.HasModelBounds)
            {
                var childBounds = child.Bounds.Transform(transform);
                bounds = bounds?.Union(childBounds) ?? childBounds;
            }
            var material = (string)obj.Attribute("Material");
            var physics = obj.Element("Properties")?.Element("Physics");
            var physicalized = (string)physics?.Attribute("bPhysicalize") != "0";
            animatedCollision |= physicalized && child.HasAnimatedCollision;
            if (active && !elapsedSeconds.HasValue)
                poses.Add(new CryGeometryPoseRequirement(childPath, (string)obj.Attribute("Name") ?? "", transform,
                    (string)animation.Attribute("Animation") ?? "", true, physicalized)
                    { AffectsCollision = child.HasAnimatedCollision });
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
        return new CryGeometryAsset(bounds ?? new CryBounds(Vector3.Zero, Vector3.Zero), parts)
        {
            HasModelBounds = bounds.HasValue,
            WaterVolumes = waterVolumes,
            Helpers = helpers,
            PoseRequirements = poses,
            HasAnimatedCollision = animatedCollision
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
        foreach (var (id, chunk) in chunks.ToArray())
        {
            if (chunk.Size != 0)
                continue;
            var end = chunks.Values.Where(other => other.Body > chunk.Body).Select(other => other.Body - 16)
                .Append(table > chunk.Body ? table : data.Length).Min();
            chunks[id] = chunk with { Size = end - chunk.Body + 16 };
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
            Seek(reader, chunk.Body + 188, 12);
            var controllers = new int[3];
            var hasController = false;
            for (var i = 0; i < 3; i++)
            {
                var controller = reader.ReadInt32();
                controllers[i] = controller;
                hasController |= controller >= 0 && chunks.ContainsKey(controller);
            }
            nodes.Add(id, new Node(name, mesh, parent, material, matrix, hasController, controllers));
        }

        Matrix4x4 Transform(int id, HashSet<int> chain)
        {
            if (!chain.Add(id) || !nodes.TryGetValue(id, out var node))
                throw new InvalidDataException("Invalid CGF node hierarchy.");
            return node.Parent == -1 ? node.Transform : node.Transform * Transform(node.Parent, chain);
        }

        bool HasController(int id, HashSet<int> chain)
        {
            if (!chain.Add(id) || !nodes.TryGetValue(id, out var node))
                throw new InvalidDataException("Invalid CGF node hierarchy.");
            return node.HasController || (node.Parent != -1 && HasController(node.Parent, chain));
        }

        var modelNodes = nodes.Where(pair => chunks.TryGetValue(pair.Value.Mesh, out var chunk) &&
            chunk.Kind == 0xcccc0000 && !pair.Value.Name.StartsWith('$') &&
            !pair.Value.Name.Contains("PhysicsProxy", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
        var merged = mergeAll || (modelNodes.Length <= 1 && !nodes.Values.Any(node =>
            node.Name.StartsWith("$joint", StringComparison.Ordinal) || node.Name.StartsWith("$cutdown", StringComparison.Ordinal)));
        CryBounds? bounds = null;
        var renderBounds = new List<CryCgaRenderBounds>();
        var parts = new List<CryGeometryPart>();
        // A skeletal model needs bone transforms until its compiled character physics is resolved.
        var animatedCollision = chunks.Values.Any(chunk => chunk.Kind == 0xacdc0000);
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
                renderBounds.Add(new CryCgaRenderBounds(id, localBounds, transform));
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
                animatedCollision |= HasController(id, []);
                parts.Add(new CryGeometryPart(shape, transform, 0x1000 + slot, materialPath, node.Name)
                {
                    PhysicsGroup = $"{path}#{(merged ? 0 : id)}",
                    SpineCount = merged ? spineCount : 0,
                    PickingIndex = GetPickingIndex(node.Name),
                    CgaNodeId = id
                });
            }
        }
        if (bounds == null)
            throw new InvalidDataException("CGF contains no supported model bounds.");
        IReadOnlyList<CryCharacterBone> bones = [];
        var boneChunk = chunks.Values.SingleOrDefault(chunk => chunk.Kind == 0xacdc0000);
        var proxyChunk = chunks.Values.SingleOrDefault(chunk => chunk.Kind == 0xacdc0003);
        if (boneChunk != null)
        {
            bones = CryCharacterPhysicsReader.Read(data.AsSpan(boneChunk.Body, boneChunk.Size - 16).ToArray(),
                boneChunk.Version, proxyChunk == null ? new byte[4] : data.AsSpan(proxyChunk.Body, proxyChunk.Size - 16).ToArray(),
                proxyChunk?.Version ?? 0x800);
            foreach (var bone in bones.Where(bone => bone.Shape != null && (bone.PhysicsFlags & 0xffff0000) != 0x30000))
                parts.Add(new CryGeometryPart(bone.Shape, bone.BindTransform, 0x1000, "", bone.Name)
                {
                    PhysicsGroup = $"{path}#bone{bone.Index}",
                    BoneIndex = bone.Index
                });
            animatedCollision = bones.Any(bone => bone.Shape != null);
            bounds = CryCharacterBounds.FromPose(bones.Select(bone => bone.BindTransform).ToArray());
        }
        return new CryGeometryAsset(bounds.Value, parts)
        {
            HasAnimatedCollision = animatedCollision,
            CharacterBones = bones,
            CharacterBoundsBones = bones.Count == 0 ? [] : chunks.Values.Where(chunk => chunk.Kind == 0xcccc0017)
                .SelectMany(chunk => ReadCharacterSubsetBones(data, chunk)).Distinct().Order().ToArray(),
            CgaAnimation = path.EndsWith(".cga", StringComparison.OrdinalIgnoreCase)
                ? CryCgaAnimation.Read(data, nodes.Select(pair => new CryCgaNode(pair.Key, pair.Value.Parent,
                    pair.Value.Transform, pair.Value.Controllers[0], pair.Value.Controllers[1], pair.Value.Controllers[2])
                    { Name = pair.Value.Name }).ToArray(), renderBounds)
                : null
        };
    }

    private static bool IsCharacterModelPath(string path) => path.EndsWith(".cga", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".chr", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cdf", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<int> ReadCharacterSubsetBones(byte[] data, Chunk chunk)
    {
        RequireVersion(chunk, 0x800);
        return CryCharacterBounds.ReadSubsetBones(data.AsSpan(chunk.Body, chunk.Size - 16).ToArray());
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

    private System.IO.Stream OpenFile(string path) => openFile(path) ??
        throw new FileNotFoundException($"Missing client geometry asset '{path}'.", path);

    private string FindCharacterAnimation(string modelPath, string name)
    {
        var uriSeparator = modelPath.IndexOf("://", StringComparison.Ordinal);
        if (uriSeparator >= 0)
            modelPath = modelPath[(uriSeparator + 3)..];
        if (!modelPath.EndsWith(".chr", StringComparison.Ordinal))
            throw new NotSupportedException("A compiled character animation needs a CHR model.");
        var result = _animationPaths.GetOrAdd((modelPath, name.ToLowerInvariant()), _ =>
            ReadCal(AssetPath(modelPath[..^4] + ".cal"), "", []) ?? "");
        return result.Length == 0 ? null : result;

        string ReadCal(string path, string animationDirectory, HashSet<string> chain)
        {
            if (!chain.Add(path))
                throw new InvalidDataException("Cyclic character CAL include.");
            System.IO.Stream stream;
            try { stream = OpenFile(path); }
            catch (FileNotFoundException) { return null; }
            using var input = stream;
            using var reader = new StreamReader(input);
            while (reader.ReadLine() is { } line)
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                    line = line[..comment];
                var separator = line.IndexOf('=');
                if (separator < 0)
                    continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key.Equals("#filepath", StringComparison.OrdinalIgnoreCase))
                    animationDirectory = Normalize(value).TrimEnd('/');
                else if (key.Equals("$Include", StringComparison.OrdinalIgnoreCase))
                {
                    var found = ReadCal(AssetPath(value), animationDirectory, new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase));
                    if (found != null)
                        return found;
                }
                else if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return AssetPath(animationDirectory.Length == 0 ? value : animationDirectory + "/" + value);
            }
            return null;
        }
    }

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
    private sealed record Node(string Name, int Mesh, int Parent, int Material, Matrix4x4 Transform, bool HasController, int[] Controllers);
}
