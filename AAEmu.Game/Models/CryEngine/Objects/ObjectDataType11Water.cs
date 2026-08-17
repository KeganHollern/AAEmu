using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Objects;

public class ObjectDataType11Water() : ObjectDataBase(ObjectDataType.WaterVolume)
{
    public override bool IsGeneric { get; protected init; } = false;

    private const int StartOfVariableData = 0x7B; // Variable points data starts at this offset

    /// <summary>CryEngine <c>IWaterVolumeRenderNode::EWaterVolumeType</c>.</summary>
    public WaterObjectVolumeType VolumeType { get; private set; }
    public ulong VolumeId { get; set; }
    /// <summary>Serialized render-node AABB minimum.</summary>
    public Vector3 StartPos { get; private set; } = Vector3.Zero;
    /// <summary>Serialized render-node AABB maximum.</summary>
    public Vector3 EndPos { get; private set; } = Vector3.Zero;
    private int ShapePointsCount { get; set; }
    private int PhysicsContourPointsCount { get; set; }
    /// <summary>
    /// This is a segment inside an existing water body
    /// </summary>
    public List<Vector3> ShapePointsList { get; private set; } = [];
    /// <summary>
    /// These points form the outer shape of the water body
    /// </summary>
    public List<Vector3> PhysicsContourPointsList { get; private set; } = [];
    public float Depth { get; private set; }
    public float Speed { get; private set; }
    public float SurfaceHeight { get; private set; }
    public float SurfVScale { get; set; }
    public float SurfUScale { get; set; }
    public float UTexEnd { get; set; }
    public float UTexBegin { get; set; }
    public float FogPlaneD { get; set; }
    public Vector3 FogPlaneNormal { get; set; }
    public float FogColorB { get; set; }
    public float FogColorG { get; set; }
    public float FogColorR { get; set; }
    public float FogDensity { get; set; }
    public int MaterialId { get; set; }
    
    /// <summary>
    /// Read the water data from a byte array starting at offset
    /// </summary>
    /// <param name="blockData"></param>
    /// <param name="offset"></param>
    /// <returns>Number of bytes used</returns>
    public override int ReadData(byte[] blockData, int offset)
    {
        var objectType = (ObjectDataType)BitConverter.ToInt32(blockData, offset + 0x00);
        if (objectType != PrefabType || (offset + StartOfVariableData > blockData.Length))
        {
            // Type mismatch or not enough bytes, return as error
            Data = [];
            return 0;
        }

        StartPos = GetVector3(blockData, offset + 0x04); // 3 x float @ 0x04 
        EndPos =  GetVector3(blockData, offset + 0x10);  //

        // r208022 packs the enum into one byte; the following three bytes are independent flags.
        VolumeType = (WaterObjectVolumeType)blockData[offset + 0x2B];
        VolumeId = BitConverter.ToUInt64(blockData, offset + 0x2F);
        MaterialId = BitConverter.ToInt32(blockData, offset + 0x37);
        FogDensity = BitConverter.ToSingle(blockData, offset + 0x3B);
        FogColorR = BitConverter.ToSingle(blockData, offset + 0x3F);
        FogColorG = BitConverter.ToSingle(blockData, offset + 0x43);
        FogColorB = BitConverter.ToSingle(blockData, offset + 0x47);
        FogPlaneNormal = GetVector3(blockData, offset + 0x4B);
        FogPlaneD = BitConverter.ToSingle(blockData, offset + 0x57);
        UTexBegin = BitConverter.ToSingle(blockData, offset + 0x5B);
        UTexEnd = BitConverter.ToSingle(blockData, offset + 0x5F);
        SurfUScale = BitConverter.ToSingle(blockData, offset + 0x63);
        SurfVScale = BitConverter.ToSingle(blockData, offset + 0x67);
        ShapePointsCount = BitConverter.ToInt32(blockData, offset + 0x6B);
        Depth = BitConverter.ToSingle(blockData, offset + 0x6F); // Math.Abs(EndPos.Z - StartPos.Z);
        Speed = BitConverter.ToSingle(blockData, offset + 0x73);
        PhysicsContourPointsCount = BitConverter.ToInt32(blockData, offset + 0x77);

        var centerX = (StartPos.X + EndPos.X) * 0.5f;
        var centerY = (StartPos.Y + EndPos.Y) * 0.5f;
        SurfaceHeight = GetSurfaceHeight(centerX, centerY);

        var totalObjectSize = (ShapePointsCount * 12) + (PhysicsContourPointsCount * 12) + StartOfVariableData;
        if (offset + totalObjectSize > blockData.Length)
        {
            Data = blockData.Skip(offset).ToArray();
            return Data.Length;
        }

        // Read points for inside data
        ShapePointsList = [];
        PhysicsContourPointsList = [];
        for (var i = 0; i < ShapePointsCount; i++)
            ShapePointsList.Add(GetVector3(blockData, offset + StartOfVariableData + (i * 12)));

        // Read border data
        var entryStart2 = StartOfVariableData + (ShapePointsCount * 12);
        for (var i = 0; i < PhysicsContourPointsCount; i++)
            PhysicsContourPointsList.Add(GetVector3(blockData, offset + entryStart2 + (i * 12)));

        // Zero-point volumes (e.g. bare Ocean marker): still consume the fixed header so ParseObjectBlockData advances.
        if (ShapePointsCount <= 0 && PhysicsContourPointsCount <= 0)
        {
            Data = blockData.Skip(offset).Take(StartOfVariableData).ToArray();
            return StartOfVariableData;
        }

        Data = blockData.Skip(offset).Take(totalObjectSize).ToArray();
        return Data.Length;
    }

    /// <summary>Returns the water fog-plane height at the serialized local XY coordinate.</summary>
    public float GetSurfaceHeight(float x, float y)
    {
        if (MathF.Abs(FogPlaneNormal.Z) <= 1e-6f)
            return Math.Max(EndPos.Z, StartPos.Z);

        return -(FogPlaneNormal.X * x + FogPlaneNormal.Y * y + FogPlaneD) / FogPlaneNormal.Z;
    }

}
