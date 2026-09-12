using System.Numerics;
using System.Text;
using AAEmu.Game.Models.CryEngine.Objects;

namespace AAEmu.UnitTests.Game.Models.CryEngine.Objects;

public sealed class ObjectDataType13RoadTests
{
    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(300)]
    public async Task ReadData_UsesPackedInt32CountAndPreservesFollowingRecord(int pointCount)
    {
        var record = Record(pointCount);
        var block = new byte[7 + record.Length + 132];
        record.CopyTo(block, 7);
        BitConverter.GetBytes(1).CopyTo(block, 7 + record.Length);
        var road = new ObjectDataType13Road();
        await Assert.That(road.ReadData(block, 7)).IsEqualTo(record.Length);
        await Assert.That(road.PointsList.Count).IsEqualTo(pointCount);
        if (pointCount > 0)
            await Assert.That(road.PointsList[^1]).IsEqualTo(new Vector3(pointCount - 1, 3, 4));
        await Assert.That(road.BoundsMin).IsEqualTo(new Vector3(1, 2, 3));
        await Assert.That(road.BoundsMax).IsEqualTo(new Vector3(5, 6, 7));
        await Assert.That(road.RenderFlags).IsEqualTo(0x11000u);
        await Assert.That(road.MaterialId).IsEqualTo(16);
        await Assert.That(road.TextureCoordinates).IsEqualTo(new Vector2(0.25f, 8.5f));
        await Assert.That(road.GlobalTextureCoordinates).IsEqualTo(new Vector2(2, 31.25f));
        await Assert.That(new ObjectDataType1Brush().ReadData(block, 7 + road.Data.Length)).IsEqualTo(132);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(int.MaxValue)]
    [Arguments(5)]
    public async Task ReadData_InvalidOrTruncatedCount_DoesNotConsumeThePartialRecord(int count)
    {
        var block = Record(4);
        BitConverter.GetBytes(count).CopyTo(block, 43);
        var road = new ObjectDataType13Road();
        road.ReadData(Record(4), 0);
        await Assert.That(road.ReadData(block, 0)).IsEqualTo(0);
        await Assert.That(road.Data.Length).IsEqualTo(0);
        await Assert.That(road.PointsList.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1, 67)]
    [Arguments(0, 3)]
    [Arguments(0, 66)]
    [Arguments(70, 67)]
    public async Task ReadData_InvalidBounds_ReturnsZero(int offset, int size)
    {
        var road = new ObjectDataType13Road();
        await Assert.That(road.ReadData(new byte[size], offset)).IsEqualTo(0);
    }

    [Test]
    public async Task ReadData_ExactClientRoad_Has66VerticesAndMaterial16()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_ROAD_RECORD");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_ROAD_RECORD to the extracted type13 record.");
        var road = new ObjectDataType13Road();
        await Assert.That(road.ReadData(await File.ReadAllBytesAsync(path!), 0)).IsEqualTo(859);
        await Assert.That(road.PointsList.Count).IsEqualTo(66);
        await Assert.That(road.MaterialId).IsEqualTo(16);
        await Assert.That(road.RenderFlags).IsEqualTo(0x11000u);
        await Assert.That(road.PointsList[0]).IsEqualTo(new Vector3(44.7371826171875f, 481.8914794921875f, 158.81771850585938f));
    }

    private static byte[] Record(int count)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(13);
        foreach (var value in new float[] { 1, 2, 3, 5, 6, 7, 0, 400 }) writer.Write(value);
        writer.Write(0x11000u);
        writer.Write(new byte[3]);
        writer.Write(count);
        foreach (var value in new float[] { 0.25f, 8.5f, 2, 31.25f }) writer.Write(value);
        writer.Write(16);
        for (var i = 0; i < count; i++)
        {
            writer.Write((float)i);
            writer.Write(3f);
            writer.Write(4f);
        }
        return stream.ToArray();
    }
}
