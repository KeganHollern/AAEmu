using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Objects;

public sealed class ObjectDataType2Vegetation() : ObjectDataBase(ObjectDataType.Vegetation)
{
    public Vector3 Min { get; private set; }
    public Vector3 Max { get; private set; }
    public Vector3 Position { get; private set; }
    public float Scale { get; private set; }
    public int GroupId { get; private set; }
    public byte Angle { get; private set; }

    public override int ReadData(byte[] blockData, int offset)
    {
        if (offset < 0 || offset > blockData.Length - 68 ||
            BitConverter.ToInt32(blockData, offset) != (int)ObjectDataType.Vegetation)
            return 0;
        ReadBody(blockData.AsSpan(offset + 4, 64));
        Data = blockData.AsSpan(offset, 68).ToArray();
        return 68;
    }

    public void ReadBody(ReadOnlySpan<byte> body)
    {
        if (body.Length != 64)
            throw new InvalidDataException("Invalid vegetation record size.");
        Min = ReadVector(body);
        Max = ReadVector(body[12..]);
        Position = ReadVector(body[39..]);
        Scale = Math.Clamp(BitConverter.ToSingle(body[51..]), 0.1f, 5f);
        GroupId = BitConverter.ToInt32(body[55..]);
        Angle = body[60];
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> data) => new(
        BitConverter.ToSingle(data), BitConverter.ToSingle(data[4..]), BitConverter.ToSingle(data[8..]));
}
