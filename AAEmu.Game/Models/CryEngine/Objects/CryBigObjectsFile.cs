using System.Text;

namespace AAEmu.Game.Models.CryEngine.Objects;

/// <summary>The r208022 client/big_object.dat stream has two path tables followed by flat typed records.</summary>
public sealed record CryBigObjectsFile(IReadOnlyList<string> AssetPaths, IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<ObjectDataBase> Objects)
{
    /// <summary>Leaves the stream open. Object bounds and transforms remain cell-local.</summary>
    public static CryBigObjectsFile Read(System.IO.Stream stream,
        Func<ObjectDataType, ObjectDataBase> createReader = null)
    {
        var assets = CryWorldPathTable.Read(stream, true);
        var materials = CryWorldPathTable.Read(stream, true);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var remaining = stream.Length - stream.Position;
        if (remaining > int.MaxValue)
            throw new InvalidDataException("Big-object data exceeds the supported size.");
        var bytes = reader.ReadBytes((int)remaining);
        var objects = new List<ObjectDataBase>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 4)
                throw new InvalidDataException("Truncated big-object type.");
            var type = (ObjectDataType)BitConverter.ToInt32(bytes, offset);
            if (type == ObjectDataType.Brush && bytes.Length - offset < 132)
                throw new InvalidDataException("Truncated big-object brush.");
            if (type == ObjectDataType.WaterVolume)
            {
                if (bytes.Length - offset < 123)
                    throw new InvalidDataException("Truncated big-object water header.");
                var shapeCount = BitConverter.ToInt32(bytes, offset + 107);
                var physicsCount = BitConverter.ToInt32(bytes, offset + 119);
                if (shapeCount < 0 || physicsCount < 0 ||
                    123L + ((long)shapeCount + physicsCount) * 12 > bytes.Length - offset)
                    throw new InvalidDataException("Invalid big-object water contour.");
            }
            var parsed = createReader == null ? CreateReader(type) : createReader(type);
            if (parsed == null)
                throw new InvalidDataException($"Unsupported big-object type {type}.");
            var size = parsed.ReadData(bytes, offset);
            if (size <= 0 || size > bytes.Length - offset)
                throw new InvalidDataException($"Invalid big-object record of type {type}.");
            objects.Add(parsed);
            offset += size;
        }
        return new CryBigObjectsFile(assets, materials, objects);
    }

    private static ObjectDataBase CreateReader(ObjectDataType type) => type switch
    {
        ObjectDataType.Brush => new ObjectDataType1Brush(),
        ObjectDataType.Voxel => new ObjectDataType6Voxel(),
        ObjectDataType.WaterVolume => new ObjectDataType11Water(),
        ObjectDataType.Road => new ObjectDataType13Road(),
        _ => new ObjectDataBase(type)
    };
}
