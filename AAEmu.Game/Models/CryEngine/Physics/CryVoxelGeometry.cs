using System.IO.Compression;
using System.Text;

using AAEmu.Game.Models.CryEngine.Objects;

namespace AAEmu.Game.Models.CryEngine.Physics;

public static class CryVoxelGeometry
{
    /// <summary>Reads the serialized physical mesh from the first authored voxel LOD.</summary>
    public static CryGeometryAsset Read(ObjectDataType6Voxel voxel, string source)
    {
        if (voxel.VoxelChunkType != 5 || voxel.VoxelResolution.Length != 3 ||
            voxel.VoxelResolution.Any(value => value != 32) || voxel.ModelNumLods <= 0 ||
            voxel.CompressedModelData.Length == 0)
            throw new InvalidDataException($"Unsupported voxel collision data: {source}.");
        using var compressed = new MemoryStream(voxel.CompressedModelData, false);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var data = new MemoryStream();
        inflater.CopyTo(data);
        if (data.Length != voxel.LodUnCompressedSize)
            throw new InvalidDataException($"Invalid voxel LOD size: {source}.");
        return CryGeometryResolver.ReadCgf(data.ToArray(), source);
    }

    /// <summary>Maps physical material IDs to the authored cell terrain surface names.</summary>
    public static IReadOnlyList<string> ReadSurfaceNames(byte[] data)
    {
        if (data.Length != 32 * 64)
            throw new InvalidDataException("Invalid voxel terrain surface table.");
        var names = new string[32];
        for (var i = 0; i < names.Length; i++)
        {
            var value = data.AsSpan(i * 64, 64);
            var end = value.IndexOf((byte)0);
            names[i] = Encoding.UTF8.GetString(end < 0 ? value : value[..end]);
        }
        return names;
    }
}
