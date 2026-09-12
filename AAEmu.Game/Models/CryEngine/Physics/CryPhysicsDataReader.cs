using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Reads r208022 CryPhysics version 1 authored collision geometry.</summary>
public static class CryPhysicsDataReader
{
    public static CryPhysicsShape Read(byte[] data, Vector3[] externalVertices = null,
        ushort[] externalIndices = null, byte[] externalMaterials = null)
    {
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1)
            throw new NotSupportedException("Unsupported CryPhysics geometry version.");
        stream.Position = 56;
        var surface = reader.ReadInt32();
        stream.Position = 68;
        var kind = reader.ReadInt32();
        return kind switch
        {
            0 => ReadBox(reader, surface),
            1 => ReadMesh(reader, surface, externalVertices, externalIndices, externalMaterials),
            4 => ReadSphere(reader, surface),
            5 or 6 => ReadCylinder(reader, surface, kind == 6),
            _ => throw new NotSupportedException($"Unsupported CryPhysics geometry type {kind}.")
        };
    }

    private static CryBox ReadBox(BinaryReader reader, int surface)
    {
        // Matrix4x4 uses row vectors. The serialized world-to-box rows become local-to-world rows.
        var matrix = new Matrix4x4(ReadFloat(reader), ReadFloat(reader), ReadFloat(reader), 0,
            ReadFloat(reader), ReadFloat(reader), ReadFloat(reader), 0,
            ReadFloat(reader), ReadFloat(reader), ReadFloat(reader), 0, 0, 0, 0, 1);
        var oriented = reader.ReadInt32();
        if (oriented is not (0 or 1))
            throw new InvalidDataException("Invalid CryPhysics box orientation flag.");
        var center = ReadVector(reader);
        var halfSize = ReadVector(reader);
        if (halfSize.X < 0 || halfSize.Y < 0 || halfSize.Z < 0)
            throw new InvalidDataException("Invalid CryPhysics box size.");
        ReadExactly(reader, 68); // The native loader reads a second box and its primitive count for its BVH.
        return new CryBox(center, halfSize, oriented == 0 ? Matrix4x4.Identity : matrix, surface);
    }

    private static CrySphere ReadSphere(BinaryReader reader, int surface)
    {
        var center = ReadVector(reader);
        var radius = ReadNonnegative(reader);
        ReadExactly(reader, 68);
        return new CrySphere(center, radius, surface);
    }

    private static CryCylinder ReadCylinder(BinaryReader reader, int surface, bool capsule)
    {
        var center = ReadVector(reader);
        var axis = ReadVector(reader);
        if (MathF.Abs(axis.LengthSquared() - 1) > 0.001f)
            throw new InvalidDataException("Invalid CryPhysics cylinder axis.");
        var radius = ReadNonnegative(reader);
        var halfHeight = ReadNonnegative(reader);
        reader.ReadInt32(); // Tessellation is a rendering property, not a collision approximation.
        ReadExactly(reader, 68);
        return new CryCylinder(center, axis, radius, halfHeight, capsule, surface);
    }

    private static CryTriangleMesh ReadMesh(BinaryReader reader, int surface, Vector3[] externalVertices,
        ushort[] externalIndices, byte[] externalMaterials)
    {
        var vertexCount = ReadCount(reader, 65536);
        var triangleCount = ReadCount(reader, 65536);
        reader.ReadInt32(); // Maximum vertex valency.
        var flags = reader.ReadUInt32();
        var vertexMap = ReadFlag(reader) ? ReadIndices(reader, vertexCount) : null;
        var foreignMap = ReadFlag(reader) ? ReadIndices(reader, triangleCount) : null;
        var external = foreignMap != null && (flags & 0x400000) == 0;
        Vector3[] vertices;
        if (external || (flags & 1) != 0)
        {
            if (externalVertices == null || externalVertices.Length < vertexCount)
                throw new InvalidDataException("CryPhysics mesh needs its CGF vertex stream.");
            vertices = externalVertices.AsSpan(0, vertexCount).ToArray();
            foreach (var vertex in vertices)
                CheckVector(vertex);
        }
        else
        {
            vertices = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                vertices[i] = ReadVector(reader);
        }

        ushort[] indices;
        byte[] materials;
        if (external)
        {
            if (externalIndices == null)
                throw new InvalidDataException("CryPhysics mesh needs its CGF index stream.");
            indices = new ushort[triangleCount * 3];
            materials = externalMaterials == null ? [] : new byte[triangleCount];
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var foreign = foreignMap[triangle];
                if (foreign * 3 + 2 >= externalIndices.Length ||
                    (externalMaterials != null && foreign >= externalMaterials.Length))
                    throw new InvalidDataException("Invalid CryPhysics foreign triangle index.");
                Array.Copy(externalIndices, foreign * 3, indices, triangle * 3, 3);
                if (materials.Length != 0)
                    materials[triangle] = externalMaterials[foreign];
            }
        }
        else
        {
            indices = ReadIndices(reader, triangleCount * 3);
            materials = ReadFlag(reader) ? ReadExactly(reader, triangleCount) : [];
        }
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= vertexCount)
                throw new InvalidDataException("Invalid CryPhysics vertex index.");
            if (vertexMap != null)
                indices[i] = vertexMap[indices[i]];
            if (indices[i] >= vertexCount)
                throw new InvalidDataException("Invalid CryPhysics mapped vertex index.");
        }

        ReadExactly(reader, 16); // Serialized mesh volume data.
        if (reader.ReadInt32() != 0) // Native empty-mesh branch precedes the acceleration tree.
            return new CryTriangleMesh(vertices, [], [], surface);
        return new CryTriangleMesh(vertices, indices, materials, surface);
    }

    internal static float ReadFloat(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        if (!float.IsFinite(value))
            throw new InvalidDataException("Non-finite CryPhysics coordinate.");
        return value;
    }

    internal static Vector3 ReadVector(BinaryReader reader) => new(ReadFloat(reader), ReadFloat(reader), ReadFloat(reader));

    internal static void CheckVector(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidDataException("Non-finite CryPhysics coordinate.");
    }

    internal static int ReadCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("Invalid CryPhysics element count.");
        return count;
    }

    internal static byte[] ReadExactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException("Truncated CryPhysics geometry.");
        return bytes;
    }

    private static ushort[] ReadIndices(BinaryReader reader, int count)
    {
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
            values[i] = reader.ReadUInt16();
        return values;
    }

    private static bool ReadFlag(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid CryPhysics serialization flag.")
    };

    private static float ReadNonnegative(BinaryReader reader)
    {
        var value = ReadFloat(reader);
        if (value < 0)
            throw new InvalidDataException("Negative CryPhysics dimension.");
        return value;
    }
}
