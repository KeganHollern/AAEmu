using System.Numerics;
using System.Xml.Linq;

using AAEmu.Game.Models.CryEngine.Objects;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed record CryVegetationGroup(int Id, string ModelUri, string MaterialPath, bool AlignToTerrain);

public static class CryVegetationGeometry
{
    public static IReadOnlyDictionary<int, CryVegetationGroup> ReadGroups(System.IO.Stream stream)
    {
        if (stream == null)
            return new Dictionary<int, CryVegetationGroup>();
        return XDocument.Load(stream).Root?.Element("groupList")?.Elements("group")
            .Select(group => new CryVegetationGroup((int)group.Attribute("id"),
                ((string)group.Attribute("modelFileName") ?? "").Replace('\\', '/'),
                ((string)group.Attribute("matName") ?? "").Replace('\\', '/'),
                (string)group.Attribute("bAlignToTerrain") == "1"))
            .ToDictionary(group => group.Id) ?? [];
    }

    public static CryWorldObjectInstance Create(ObjectDataType2Vegetation vegetation, CryVegetationGroup group,
        Vector3 offset, string source)
    {
        var position = vegetation.Position + offset;
        var yaw = vegetation.Angle * (360f / 255f) * (MathF.PI / 180f);
        return new CryWorldObjectInstance(group.ModelUri,
            Matrix4x4.CreateScale(vegetation.Scale) * Matrix4x4.CreateRotationZ(yaw) *
            Matrix4x4.CreateTranslation(position), vegetation.Min + offset, vegetation.Max + offset, source)
        {
            Kind = ObjectDataType.Vegetation, MaterialPath = group.MaterialPath,
            AlignToTerrain = group.AlignToTerrain
        };
    }

    /// <summary>Resolves the native terrain alignment only when a query needs the instance.</summary>
    public static Matrix4x4 ResolveTransform(CryWorldObjectInstance instance, Func<float, float, float> sampleHeight)
    {
        if (!instance.AlignToTerrain)
            return instance.Transform;
        var position = instance.Transform.Translation;
        var distance = (instance.Max - instance.Min).Length() * 0.5f + 0.05f;
        var a = sampleHeight(position.X - distance, position.Y - distance);
        var b = sampleHeight(position.X, position.Y + distance);
        var c = sampleHeight(position.X + distance, position.Y);
        var d = sampleHeight(position.X + distance, position.Y + distance);
        if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(d))
            throw new InvalidDataException($"Missing vegetation alignment terrain: {instance.Source}.");
        var diagonal1 = new Vector3(2 * distance, -2 * distance, c - b);
        var diagonal2 = new Vector3(2 * distance, 2 * distance, d - a);
        var normal = Vector3.Normalize(Vector3.Cross(diagonal1, diagonal2));
        var forward = Vector3.Normalize(new Vector3(0, normal.Z, -normal.Y));
        var right = Vector3.Normalize(Vector3.Cross(-normal, forward));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        var alignment = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            up.X, up.Y, up.Z, 0,
            0, 0, 0, 1);
        var transform = instance.Transform;
        transform.Translation = Vector3.Zero;
        transform *= alignment;
        transform.Translation = position;
        return transform;
    }

    public static IEnumerable<CryWorldObjectInstance> ReadStreamed(System.IO.Stream stream, int cellX, int cellY,
        IReadOnlyDictionary<int, CryVegetationGroup> groups, string source, Func<string, bool> includeModel = null)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        if (stream.Length < 132 || reader.ReadInt32() != 1)
            throw new InvalidDataException($"Invalid vegetation file: {source}.");
        var offsets = new int[16];
        var sizes = new int[16];
        for (var i = 0; i < 16; i++) offsets[i] = reader.ReadInt32();
        for (var i = 0; i < 16; i++) sizes[i] = reader.ReadInt32();
        for (var sector = 0; sector < 16; sector++)
        {
            var size = sizes[sector];
            if (size < 0 || size % 64 != 0 || offsets[sector] < 0 || offsets[sector] > stream.Length - size)
                throw new InvalidDataException($"Invalid vegetation sector: {source}.");
            if (size == 0)
                continue;
            stream.Position = offsets[sector];
            var offset = new Vector3(cellX * 1024 + sector / 4 * 256, cellY * 1024 + sector % 4 * 256, 0);
            for (var row = 0; row < size / 64; row++)
            {
                var data = reader.ReadBytes(64);
                var groupId = BitConverter.ToInt32(data, 55);
                // Native _LoadVegetation skips deleted groups and groups with no model.
                if (!groups.TryGetValue(groupId, out var group) || string.IsNullOrEmpty(group.ModelUri) ||
                    includeModel?.Invoke(group.ModelUri) == false)
                    continue;
                var vegetation = new ObjectDataType2Vegetation();
                vegetation.ReadBody(data);
                yield return Create(vegetation, group, offset, source);
            }
        }
    }
}
