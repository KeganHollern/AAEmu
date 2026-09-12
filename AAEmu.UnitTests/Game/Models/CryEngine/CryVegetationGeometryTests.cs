using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.CryEngine;

public sealed class CryVegetationGeometryTests
{
    [Test]
    public async Task TypedAndStreamedRecords_ShareNativeFieldsAndSectorCoordinates()
    {
        var body = Body(7, 90, 2);
        var typed = new byte[68];
        BitConverter.GetBytes(2).CopyTo(typed, 0);
        body.CopyTo(typed, 4);
        var vegetation = new ObjectDataType2Vegetation();
        await Assert.That(vegetation.ReadData(typed, 0)).IsEqualTo(68);
        await Assert.That(vegetation.Position).IsEqualTo(new Vector3(10, 20, 30));
        await Assert.That(vegetation.Scale).IsEqualTo(2);
        await Assert.That(vegetation.GroupId).IsEqualTo(7);
        await Assert.That(vegetation.Angle).IsEqualTo((byte)90);
        using var stream = Streamed(body, 6);
        var row = CryVegetationGeometry.ReadStreamed(stream, 2, 3, Groups(), "test").Single();
        await Assert.That(row.Transform.Translation).IsEqualTo(new Vector3(2314, 3604, 30));
        await Assert.That(row.Min).IsEqualTo(new Vector3(2304, 3584, 0));
        await Assert.That(row.Max).IsEqualTo(new Vector3(2344, 3624, 40));
        await Assert.That(row.MaterialPath).IsEqualTo("materials/tree");
    }

    [Test]
    public async Task DeletedGroupAndModelFilter_SkipUnusedRecords()
    {
        using var deleted = Streamed(Body(999, 0, 1), 0);
        await Assert.That(CryVegetationGeometry.ReadStreamed(deleted, 0, 0, Groups(), "deleted").Count()).IsEqualTo(0);
        using var noProxy = Streamed(Body(7, 0, 1), 0);
        await Assert.That(CryVegetationGeometry.ReadStreamed(noProxy, 0, 0, Groups(), "no proxy", _ => false).Count()).IsEqualTo(0);
    }

    [Test]
    public async Task NativeScaleClampAndFullTurn_PreserveTheAuthoredTransform()
    {
        var vegetation = new ObjectDataType2Vegetation();
        vegetation.ReadBody(Body(7, 255, 10));
        var row = CryVegetationGeometry.Create(vegetation, Groups()[7], Vector3.Zero, "test");
        await Assert.That(vegetation.Scale).IsEqualTo(5);
        await Assert.That(Vector3.Distance(Vector3.Transform(Vector3.UnitX, row.Transform), new Vector3(15, 20, 30)))
            .IsLessThan(0.0001f);
        vegetation.ReadBody(Body(7, 0, 0));
        await Assert.That(vegetation.Scale).IsEqualTo(0.1f);
    }

    [Test]
    public async Task Alignment_UsesNativeFourSamplesAndKeepsTranslation()
    {
        var row = new CryWorldObjectInstance("tree", Matrix4x4.CreateTranslation(10, 20, 30),
            Vector3.Zero, new Vector3(2), "test") { AlignToTerrain = true };
        var samples = new List<Vector2>();
        var transform = CryVegetationGeometry.ResolveTransform(row, (x, y) =>
        {
            samples.Add(new Vector2(x, y));
            return x;
        });
        var radius = MathF.Sqrt(12) * 0.5f + 0.05f;
        await Assert.That(samples[0]).IsEqualTo(new Vector2(10 - radius, 20 - radius));
        await Assert.That(samples[1]).IsEqualTo(new Vector2(10, 20 + radius));
        await Assert.That(samples[2]).IsEqualTo(new Vector2(10 + radius, 20));
        await Assert.That(samples[3]).IsEqualTo(new Vector2(10 + radius, 20 + radius));
        await Assert.That(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, transform),
            Vector3.Normalize(new Vector3(-0.75f, -0.25f, 1)))).IsLessThan(0.00001f);
        await Assert.That(transform.Translation).IsEqualTo(new Vector3(10, 20, 30));
        var flat = CryVegetationGeometry.ResolveTransform(row, (_, _) => 40);
        await Assert.That(flat).IsEqualTo(row.Transform);
    }

    [Test]
    public async Task VegetationLayers_KeepOnlySolidForHousingQueries()
    {
        await Assert.That(CryGeometryLayerRules.GetVegetationUsage(0x1000))
            .IsEqualTo(CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap);
        foreach (var type in new[] { 0x1001, 0x1002, 0x1003 })
            await Assert.That(CryGeometryLayerRules.GetVegetationUsage(type)).IsEqualTo(CryGeometryQueryUsage.None);
    }

    private static IReadOnlyDictionary<int, CryVegetationGroup> Groups()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <vegetationMgr><groupList><group id="7" modelFileName="objects/tree.cgf"
            matName="materials/tree" bAlignToTerrain="1"/></groupList></vegetationMgr>
            """));
        return CryVegetationGeometry.ReadGroups(stream);
    }

    private static byte[] Body(int group, byte angle, float scale)
    {
        var data = new byte[64];
        for (var axis = 0; axis < 3; axis++) BitConverter.GetBytes(40f).CopyTo(data, 12 + axis * 4);
        BitConverter.GetBytes(10f).CopyTo(data, 39);
        BitConverter.GetBytes(20f).CopyTo(data, 43);
        BitConverter.GetBytes(30f).CopyTo(data, 47);
        BitConverter.GetBytes(scale).CopyTo(data, 51);
        BitConverter.GetBytes(group).CopyTo(data, 55);
        data[60] = angle;
        return data;
    }

    private static MemoryStream Streamed(byte[] body, int sector)
    {
        var data = new byte[132 + 64];
        BitConverter.GetBytes(1).CopyTo(data, 0);
        BitConverter.GetBytes(132).CopyTo(data, 4 + sector * 4);
        BitConverter.GetBytes(64).CopyTo(data, 68 + sector * 4);
        body.CopyTo(data, 132);
        return new MemoryStream(data, false);
    }
}
