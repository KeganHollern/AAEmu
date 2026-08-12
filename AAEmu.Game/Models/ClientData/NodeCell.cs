namespace AAEmu.Game.Models.ClientData;

public class NodeCell
{
    private const int FullResolution = 32;
    private const int Inv5Cm = 20;
    private const uint Mask12Bit = (1 << 12) - 1;

    public byte Version { get; set; }
    public byte Dummy { get; set; }
    public byte Flags { get; set; }
    public byte Flags2 { get; set; }
    public AABB BoxHeightmap { get; set; } = new();
    public byte bHasHoles { get; set; }
    public float fOffset { get; set; }
    public float fRange { get; set; }
    public int nSize { get; set; }
    public ushort[] pHMData { get; set; }

    private int iOffset;
    private int iRange;
    private int iStep;
    private float fMin;
    private float fMax;

    public void Read(BinaryReader br, bool disabledReCalc = false)
    {
        Version = br.ReadByte();
        Dummy = br.ReadByte();
        Flags = br.ReadByte();
        Flags2 = br.ReadByte();

        BoxHeightmap.Min.X = br.ReadSingle();
        BoxHeightmap.Min.Y = br.ReadSingle();
        BoxHeightmap.Min.Z = br.ReadSingle();
        BoxHeightmap.Max.X = br.ReadSingle();
        BoxHeightmap.Max.Y = br.ReadSingle();
        BoxHeightmap.Max.Z = br.ReadSingle();

        bHasHoles = br.ReadByte();
        fOffset = br.ReadSingle();

        fRange = br.ReadSingle();
        nSize = br.ReadInt32();
        pHMData = new ushort[nSize * nSize];

        var unkCount = br.ReadInt32();

        for (var i = 0; i < pHMData.Length; i++)
            pHMData[i] = br.ReadUInt16();

        br.ReadInt32();
        br.ReadSingle();
        br.ReadSingle();
        br.ReadSingle();
        br.ReadSingle();

        br.ReadBytes(36 + unkCount);

        Init();
        if (!disabledReCalc && Version < 7)
            RescaleToInt();
    }

    public float RawDataToHeight(uint data)
    {
        return 0.05f * iOffset + (data >> 4) * iStep * 0.05f;
    }

    public ushort RawDataByIndex(uint i)
    {
        return pHMData[i];
    }

    public ushort RawDataByIndex(ushort nX, ushort nY)
    {
        if (nSize > 0)
        {
            var index = nX * nSize + nY;
            if (index >= pHMData.Length)
                return 0;

            return pHMData[index];
        }

        return 0;
    }

    public float GetHeightByIndex(uint i)
    {
        return RawDataToHeight(pHMData[i]);
    }

    public float GetHeight(ushort nX, ushort nY)
    {
        if (nSize > 0)
        {
            var index = nX * nSize + nY;
            return GetHeightByIndex((uint)index);
        }

        return 0f;
    }

    /// <summary>
    /// Samples the node's native height grid at a full-resolution sector coordinate.
    /// Packed surface flags are excluded before interpolating the decoded heights.
    /// </summary>
    public float GetHeightAtUnit(ushort unitX, ushort unitY)
    {
        if (nSize <= 0)
            return 0f;

        var sourceScale = (nSize - 1) / (float)FullResolution;
        var sourceX = unitX * sourceScale;
        var sourceY = unitY * sourceScale;
        var sourceX0 = (ushort)MathF.Floor(sourceX);
        var sourceY0 = (ushort)MathF.Floor(sourceY);
        var sourceX1 = (ushort)Math.Min(sourceX0 + 1, nSize - 1);
        var sourceY1 = (ushort)Math.Min(sourceY0 + 1, nSize - 1);

        return Blerp(
            GetHeight(sourceX0, sourceY0),
            GetHeight(sourceX1, sourceY0),
            GetHeight(sourceX0, sourceY1),
            GetHeight(sourceX1, sourceY1),
            sourceX - sourceX0,
            sourceY - sourceY0);
    }

    private void Init()
    {
        fMin = fOffset;
        fMax = fMin + 0xFFF0 * fRange;

        iOffset = (int)(fMin * Inv5Cm);
        iRange = (int)((fMax - fMin) * Inv5Cm);
        iStep = (int)(iRange > 0 ? (iRange + Mask12Bit - 1) / Mask12Bit : 1);
    }

    private void RescaleToInt()
    {
        for (var i = 0; i < pHMData.Length; i++)
        {
            var hraw = pHMData[i];

            var height = fMin + (0xFFF0 & hraw) * fRange;
            var hdec = (ushort)((int)((height - fMin) * Inv5Cm) / iStep);

            var res = (hraw & 0xF) | (hdec << 4);
            pHMData[i] = (ushort)res;
        }
    }

    private static float Lerp(float s, float e, float t)
    {
        return s + (e - s) * t;
    }

    private static float Blerp(float cX0Y0, float cX1Y0, float cX0Y1, float cX1Y1, float tx, float ty)
    {
        return Lerp(Lerp(cX0Y0, cX1Y0, tx), Lerp(cX0Y1, cX1Y1, tx), ty);
    }

}
