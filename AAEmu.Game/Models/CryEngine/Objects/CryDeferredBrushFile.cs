using System.Numerics;
using System.Text;

namespace AAEmu.Game.Models.CryEngine.Objects;

public sealed record CryDeferredBrushInstance(ObjectDataType1Brush Brush, string AssetPath,
    string MaterialPath, int SectorX, int SectorY);

/// <summary>The r208022 client/brush.dat stream and its statobjs.dat/materials.dat path tables.</summary>
public static class CryDeferredBrushFile
{
    private const int SectorCount = 16 * 16;
    private const int HeaderSize = 4 + SectorCount * 8;
    private const int RecordSize = 128;

    /// <summary>Returns brushes with cell-local bounds and transforms. Leaves all streams open.</summary>
    public static IReadOnlyList<CryDeferredBrushInstance> Read(System.IO.Stream brush,
        System.IO.Stream statobjs, System.IO.Stream materials)
    {
        var assets = CryWorldPathTable.Read(statobjs, false);
        var materialPaths = CryWorldPathTable.Read(materials, false);
        using var reader = new BinaryReader(brush, Encoding.UTF8, true);
        var start = brush.Position;
        if (brush.Length - start < HeaderSize)
            throw new InvalidDataException("Truncated deferred brush header.");
        if (reader.ReadInt32() != 1)
            throw new InvalidDataException("Unsupported deferred brush version.");
        var offsets = new int[SectorCount];
        var sizes = new int[SectorCount];
        for (var i = 0; i < SectorCount; i++) offsets[i] = reader.ReadInt32();
        for (var i = 0; i < SectorCount; i++) sizes[i] = reader.ReadInt32();
        var result = new List<CryDeferredBrushInstance>();
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var offset = offsets[sector];
            var size = sizes[sector];
            if (size < 0 || size % RecordSize != 0 ||
                size != 0 && (offset < HeaderSize || (long)offset + size > brush.Length - start))
                throw new InvalidDataException("Invalid deferred brush sector range.");
            if (size == 0)
                continue;
            brush.Position = start + offset;
            var x = sector / 16;
            var y = sector % 16;
            for (var i = 0; i < size / RecordSize; i++)
            {
                // A deferred record omits the four-byte type field of a normal brush record.
                var bytes = new byte[RecordSize + 4];
                BitConverter.TryWriteBytes(bytes, (int)ObjectDataType.Brush);
                brush.ReadExactly(bytes.AsSpan(4));
                var pathId = BitConverter.ToInt32(bytes, 0x7f);
                var materialId = BitConverter.ToInt32(bytes, 0x77);
                if ((uint)pathId >= assets.Count || (uint)materialId >= materialPaths.Count)
                    throw new InvalidDataException("Invalid deferred brush model or material reference.");
                // Native 3015ff10 supplies the sector origin to the ordinary brush loader 301f2ca0.
                Add(bytes, 4, x * 64);
                Add(bytes, 8, y * 64);
                Add(bytes, 16, x * 64);
                Add(bytes, 20, y * 64);
                Add(bytes, 0x53, x * 64);
                Add(bytes, 0x63, y * 64);
                var parsed = new ObjectDataType1Brush();
                parsed.ReadData(bytes, 0);
                result.Add(new CryDeferredBrushInstance(parsed, assets[pathId], materialPaths[materialId], x, y));
            }
        }
        return result;
    }

    private static void Add(byte[] bytes, int offset, float delta) =>
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), BitConverter.ToSingle(bytes, offset) + delta);
}

internal static class CryWorldPathTable
{
    public static IReadOnlyList<string> Read(System.IO.Stream stream, bool hasFlags)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var count = reader.ReadInt32();
        var recordSize = hasFlags ? 260 : 256;
        if (count < 0 || (long)count * recordSize > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid world model or material table.");
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (hasFlags) reader.ReadUInt32();
            var bytes = reader.ReadBytes(256);
            var end = Array.IndexOf(bytes, (byte)0);
            paths[i] = Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end).TrimEnd();
        }
        return paths;
    }
}
