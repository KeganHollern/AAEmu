using System.Buffers.Binary;

namespace AAEmu.Game.Models.Stream;

public sealed class UccUploadHandle(int expectedSize, CustomUcc uploadingUcc)
{
    public const int PartSize = 3096;
    public const int MinimumDdsSize = 136;
    public const int MaximumDdsSize = 87536;
    private readonly List<byte> _data = [];
    private uint _nextIndex;
    public bool UploadComplete => _data.Count == expectedSize;

    public bool TryAddPart(UccPart part)
    {
        if (expectedSize is < MinimumDdsSize or > MaximumDdsSize || part?.Data == null ||
            part.Total != expectedSize || part.Index != _nextIndex || part.Size != part.Data.Length ||
            part.Size != Math.Min(PartSize, expectedSize - _data.Count) || part.Size <= 0)
            return false;
        _data.AddRange(part.Data);
        _nextIndex++;
        return true;
    }

    public bool TryFinalizeUpload()
    {
        if (!UploadComplete || !IsValidDds(_data.ToArray()))
            return false;
        uploadingUcc.Data = [.. _data];
        return true;
    }

    internal static bool IsValidDds(ReadOnlySpan<byte> data)
    {
        if (data.Length is < MinimumDdsSize or > MaximumDdsSize || Read(data, 0) != 0x20534444 ||
            Read(data, 4) != 124 || Read(data, 76) != 32 || Read(data, 80) != 4 ||
            Read(data, 24) > 1 || Read(data, 112) != 0)
            return false;
        var width = Read(data, 16);
        var height = Read(data, 12);
        if (width is 0 or > 256 || height is 0 or > 256 || (width & (width - 1)) != 0 || (height & (height - 1)) != 0)
            return false;
        var fourCc = Read(data, 84);
        var blockSize = fourCc switch
        {
            0x31545844 => 8u, // DXT1
            0x33545844 or 0x35545844 or 0x32495441 => 16u, // DXT3, DXT5, ATI2
            _ => 0u
        };
        var mipCount = Read(data, 28);
        var maximumMipCount = 1;
        for (var dimension = Math.Max(width, height); dimension > 1; dimension >>= 1)
            maximumMipCount++;
        if (blockSize == 0 || mipCount == 0 || mipCount > maximumMipCount)
            return false;
        var length = 128u;
        for (var mip = 0; mip < mipCount; mip++)
        {
            length += ((width + 3) / 4) * ((height + 3) / 4) * blockSize;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return length == data.Length;
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
