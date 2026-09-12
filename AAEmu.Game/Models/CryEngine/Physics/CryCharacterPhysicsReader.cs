using System.Numerics;
using System.Text;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed record CryCharacterBone(int Index, uint ControllerId, string Name, int ParentIndex,
    Matrix4x4 BindTransform, CryPhysicsShape Shape, int PhysicsFlags);

/// <summary>Reads r208022 compiled character bone geometry for physics LOD zero.</summary>
public static class CryCharacterPhysicsReader
{
    public static IReadOnlyList<CryCharacterBone> Read(byte[] boneData, int boneVersion,
        byte[] proxyData, int proxyVersion)
    {
        var proxies = ReadProxies(proxyData, proxyVersion);
        var stride = boneVersion switch
        {
            0x800 => 584,
            0x801 => 324,
            _ => throw new NotSupportedException($"Unsupported compiled bone version {boneVersion:X}.")
        };
        if (boneData.Length < 32 || (boneData.Length - 32) % stride != 0)
            throw new InvalidDataException("Invalid compiled bone array.");
        using var reader = new BinaryReader(new MemoryStream(boneData, false));
        var bones = new List<CryCharacterBone>();
        for (var start = 32; start < boneData.Length; start += stride)
        {
            reader.BaseStream.Position = start + (boneVersion == 0x801 ? 0 : 4);
            var proxy = reader.ReadInt32();
            var flags = reader.ReadInt32();
            reader.BaseStream.Position = start + (boneVersion == 0x801 ? 208 : 0);
            var controller = reader.ReadUInt32();
            reader.BaseStream.Position = start + (boneVersion == 0x801 ? 216 : 312);
            var name = Encoding.UTF8.GetString(CryPhysicsDataReader.ReadExactly(reader, boneVersion == 0x801 ? 48 : 256)).Split('\0')[0];
            reader.BaseStream.Position = start + (boneVersion == 0x801 ? 264 : 572);
            var parentOffset = reader.ReadInt32();
            var parent = parentOffset == 0 ? -1 : bones.Count + parentOffset;
            if (parent < -1 || parent >= bones.Count)
                throw new InvalidDataException("Invalid compiled bone parent.");
            reader.BaseStream.Position = start + (boneVersion == 0x801 ? 276 : 264);
            var values = new float[12];
            for (var i = 0; i < values.Length; i++)
                values[i] = CryPhysicsDataReader.ReadFloat(reader);
            // Authored Matrix34 is a column-vector bone-to-world transform, already in meters.
            var transform = new Matrix4x4(values[0], values[4], values[8], 0,
                values[1], values[5], values[9], 0, values[2], values[6], values[10], 0,
                values[3], values[7], values[11], 1);
            CryPhysicsShape shape = null;
            // Native3161c640 clears the pointer before lookup and keeps it null for an absent ID.
            if (proxy > 0)
                proxies.Remove(proxy, out shape);
            bones.Add(new CryCharacterBone(bones.Count, controller, name, parent, transform, shape, flags));
        }
        return bones;
    }

    private static Dictionary<int, CryPhysicsShape> ReadProxies(byte[] data, int version)
    {
        if (version is not (0x800 or 0x801))
            throw new NotSupportedException($"Unsupported compiled character proxy version {version:X}.");
        using var reader = new BinaryReader(new MemoryStream(data, false));
        var count = CryPhysicsDataReader.ReadCount(reader, 65536);
        var proxies = new Dictionary<int, CryPhysicsShape>();
        for (var index = 0; index < count; index++)
        {
            var id = reader.ReadInt32();
            var vertices = CryPhysicsDataReader.ReadCount(reader, 65536);
            var indices = CryPhysicsDataReader.ReadCount(reader, int.MaxValue);
            var materials = CryPhysicsDataReader.ReadCount(reader, int.MaxValue);
            var kind = version == 0x801 ? reader.ReadByte() : 1;
            CryPhysicsShape shape;
            if (kind is 0 or 4 or 5 or 6)
            {
                shape = ReadPrimitive(reader, kind);
                var material = reader.ReadByte();
                shape = shape with { SurfaceIndex = material };
            }
            else
            {
                if (indices % 3 != 0 || indices > data.Length / 2 || materials > data.Length ||
                    (materials != 0 && materials != indices / 3))
                    throw new InvalidDataException("Invalid compiled character triangle counts.");
                var points = new Vector3[vertices];
                for (var i = 0; i < points.Length; i++)
                    points[i] = CryPhysicsDataReader.ReadVector(reader);
                var triangles = new ushort[indices];
                for (var i = 0; i < triangles.Length; i++)
                {
                    triangles[i] = reader.ReadUInt16();
                    if (triangles[i] >= vertices)
                        throw new InvalidDataException("Invalid compiled character triangle index.");
                }
                shape = new CryTriangleMesh(points, triangles, CryPhysicsDataReader.ReadExactly(reader, materials));
            }
            proxies.Add(id, shape);
        }
        if (reader.BaseStream.Length - reader.BaseStream.Position > 3)
            throw new InvalidDataException("Trailing compiled character proxy data.");
        return proxies;
    }

    private static CryPhysicsShape ReadPrimitive(BinaryReader reader, int kind)
    {
        if (kind == 0)
        {
            var matrix = new float[9];
            for (var i = 0; i < matrix.Length; i++)
                matrix[i] = CryPhysicsDataReader.ReadFloat(reader);
            var oriented = reader.ReadInt32();
            if (oriented is not (0 or 1))
                throw new InvalidDataException("Invalid character box orientation flag.");
            var center = CryPhysicsDataReader.ReadVector(reader);
            var half = CryPhysicsDataReader.ReadVector(reader);
            if (half.X < 0 || half.Y < 0 || half.Z < 0)
                throw new InvalidDataException("Invalid character box size.");
            var basis = oriented == 0 ? Matrix4x4.Identity : new Matrix4x4(matrix[0], matrix[1], matrix[2], 0,
                matrix[3], matrix[4], matrix[5], 0, matrix[6], matrix[7], matrix[8], 0, 0, 0, 0, 1);
            return new CryBox(center, half, basis);
        }
        var position = CryPhysicsDataReader.ReadVector(reader);
        var axis = kind == 4 ? Vector3.Zero : CryPhysicsDataReader.ReadVector(reader);
        var radius = CryPhysicsDataReader.ReadFloat(reader);
        if (radius < 0)
            throw new InvalidDataException("Invalid character primitive radius.");
        if (kind == 4)
            return new CrySphere(position, radius);
        var halfHeight = CryPhysicsDataReader.ReadFloat(reader);
        if (halfHeight < 0 || MathF.Abs(axis.LengthSquared() - 1) > 0.001f)
            throw new InvalidDataException("Invalid character cylinder dimensions.");
        return new CryCylinder(position, axis, radius, halfHeight, kind == 6);
    }
}
