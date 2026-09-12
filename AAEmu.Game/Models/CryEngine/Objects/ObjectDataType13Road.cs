using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Objects;

public class ObjectDataType13Road() : ObjectDataBase(ObjectDataType.Road)
{
    private const int HeaderSize = 67;
    public override bool IsGeneric { get; protected init; } = false;
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public uint RenderFlags { get; private set; }
    public int MaterialId { get; private set; }
    public Vector2 TextureCoordinates { get; private set; }
    public Vector2 GlobalTextureCoordinates { get; private set; }
    public List<Vector3> PointsList { get; private set; } = [];

    public override int ReadData(byte[] blockData, int offset)
    {
        Data = [];
        PointsList = [];
        BoundsMin = BoundsMax = Vector3.Zero;
        RenderFlags = 0;
        MaterialId = 0;
        TextureCoordinates = GlobalTextureCoordinates = Vector2.Zero;
        if (offset < 0 || offset > blockData.Length - HeaderSize ||
            (ObjectDataType)BitConverter.ToInt32(blockData, offset) != PrefabType)
            return 0;

        // r208022 SRoadChunk_NEW: the 4-byte object type precedes a packed 63-byte header.
        // m_nVertsNum is an int, not a byte. Reject partial roads so subsequent records stay aligned.
        var count = BitConverter.ToInt32(blockData, offset + 43);
        if (count < 0 || count > (blockData.Length - offset - HeaderSize) / 12)
            return 0;
        var totalObjectSize = HeaderSize + count * 12;
        BoundsMin = GetVector3(blockData, offset + 4);
        BoundsMax = GetVector3(blockData, offset + 16);
        RenderFlags = BitConverter.ToUInt32(blockData, offset + 36);
        TextureCoordinates = new Vector2(BitConverter.ToSingle(blockData, offset + 47), BitConverter.ToSingle(blockData, offset + 51));
        GlobalTextureCoordinates = new Vector2(BitConverter.ToSingle(blockData, offset + 55), BitConverter.ToSingle(blockData, offset + 59));
        MaterialId = BitConverter.ToInt32(blockData, offset + 63);
        for (var i = 0; i < count; i++)
            PointsList.Add(GetVector3(blockData, offset + HeaderSize + i * 12));
        Data = blockData.AsSpan(offset, totalObjectSize).ToArray();
        return Data.Length;
    }
}
