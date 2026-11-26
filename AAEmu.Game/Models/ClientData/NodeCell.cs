using System.Collections;

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

    internal BitArray TerrainHoles { get; private set; }

    internal float LegacyBaseHeight { get; private set; }
    internal int LegacyStep { get; private set; }

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

        // Surface 31 removes the square. Height rescaling below retains only four of its five bits.
        if (bHasHoles != 0 && nSize > 1)
        {
            for (var x = 0; x < FullResolution; x++)
            for (var y = 0; y < FullResolution; y++)
            {
                var sourceX = x * (nSize - 1) / FullResolution;
                var sourceY = y * (nSize - 1) / FullResolution;
                if ((pHMData[sourceX * nSize + sourceY] & 31) != 31)
                    continue;

                TerrainHoles ??= new BitArray(FullResolution * FullResolution);
                TerrainHoles[x * FullResolution + y] = true;
            }
        }

        Init();
        if (!disabledReCalc)
            RescaleForLegacyHeightMap();
    }

    public float RawDataToHeight(uint data)
    {
        return DecodeLegacyHeight(data >> 4, LegacyBaseHeight, LegacyStep);
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
        var maximum = fOffset + 0xfff0 * fRange;
        var range = (int)((maximum - fOffset) * Inv5Cm);
        LegacyBaseHeight = 0.05f * (int)(fOffset * Inv5Cm);
        LegacyStep = (int)(range > 0 ? (range + Mask12Bit - 1) / Mask12Bit : 1);
    }

    internal void RescaleForLegacyHeightMap()
    {
        if (Version >= 7)
            return;
        for (var i = 0; i < pHMData.Length; i++)
        {
            var raw = pHMData[i];
            pHMData[i] = (ushort)((raw & 0xfu) | (GetLegacyBin(raw, fOffset, fRange, LegacyStep) << 4));
        }
    }

    internal static uint GetLegacyBin(ushort sample, float offset, float range, int step)
    {
        var height = offset + (sample & 0xfff0) * range;
        var distance = (int)((height - offset) * Inv5Cm);
        return (uint)(step == 1 ? distance : distance / step) & Mask12Bit;
    }

    internal static float DecodeLegacyHeight(uint bin, float baseHeight, int step) =>
        baseHeight + bin * step * 0.05f;

    private static float Lerp(float s, float e, float t)
    {
        return s + (e - s) * t;
    }

    private static float Blerp(float cX0Y0, float cX1Y0, float cX0Y1, float cX1Y1, float tx, float ty)
    {
        return Lerp(Lerp(cX0Y0, cX1Y0, tx), Lerp(cX0Y1, cX1Y1, tx), ty);
    }

}
