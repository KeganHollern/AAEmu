using System.Numerics;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingWaterGeometryTests
{
    [Test]
    public async Task SmallAuthoredArea_RemainsWaterForHousing()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(), Vector3.Zero);
        await Assert.That(geometry.Count).IsEqualTo(1);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0)).IsEqualTo(10f);
        await Assert.That(geometry.GetWaterLevel(new(6, 2, 9), 0)).IsEqualTo(0f);
    }

    [Test]
    [Arguments(5f, 0f)]
    [Arguments(5.001f, 10f)]
    [Arguments(20.009f, 10f)]
    [Arguments(20.01f, 0f)]
    public async Task Area_UsesStrictDepthAndNativeUpperExtent(float z, float expected)
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(), Vector3.Zero);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, z), 0)).IsEqualTo(expected);
    }

    [Test]
    public async Task Overlap_UsesMaximumOfSelectedVolumesAndOcean()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(size: 8), Vector3.Zero);
        geometry.Add(Water(id: 2, surface: 12), Vector3.Zero);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0)).IsEqualTo(12f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 13)).IsEqualTo(13f);
    }

    [Test]
    public async Task Overlap_FourNewestMatchesExcludeTheOlderHigherSurface()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(id: 1, surface: 30, depth: 60), Vector3.Zero);
        for (var i = 2; i <= 5; i++)
            geometry.Add(Water(id: (ulong)i, surface: i + 8, depth: 60), Vector3.Zero);
        // A newer nonmatching volume does not consume one of the 4 native result slots.
        geometry.Add(Water(id: 6, surface: 40, depth: 60), new Vector3(100, 0, 0));
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0)).IsEqualTo(13f);
    }

    [Test]
    public async Task SameVolumeId_DoesNotAddADifferentContour()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(), Vector3.Zero);
        geometry.Add(Water(surface: 20), new Vector3(100, 0, 0));
        await Assert.That(geometry.Count).IsEqualTo(1);
        await Assert.That(geometry.GetWaterLevel(new(102, 2, 19), 0)).IsEqualTo(0f);
    }

    [Test]
    public async Task ConcavePhysicsContour_DoesNotFillItsDryNotch()
    {
        var water = Water(size: 6);
        water.PhysicsContourPointsList.Clear();
        water.PhysicsContourPointsList.AddRange([new(0, 0, 10), new(6, 0, 10), new(6, 6, 10),
            new(4, 6, 10), new(4, 2, 10), new(2, 2, 10), new(2, 6, 10), new(0, 6, 10)]);
        var geometry = new HousingWaterGeometry();
        geometry.Add(water, Vector3.Zero);
        await Assert.That(geometry.GetWaterLevel(new(1, 4, 9), 0)).IsEqualTo(10f);
        await Assert.That(geometry.GetWaterLevel(new(3, 4, 9), 0)).IsEqualTo(0f);
    }

    [Test]
    public async Task River_ProjectsRawHeightsToItsCorrectedFogPlane()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(type: WaterObjectVolumeType.River, surface: 13, size: 8,
            normal: new Vector3(0.6f, 0, 0.8f), rawPhysics: true), Vector3.Zero);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0)).IsEqualTo(10.6f).Within(0.0001f);
        // Native depth is along the plane normal. The vertical depth at this point exceeds 5 metres.
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 6), 0)).IsEqualTo(9.52f).Within(0.0001f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 12), 0)).IsEqualTo(0f);
    }

    [Test]
    public async Task CellOffset_UsesTheFileCellForWorldCoordinates()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(), new Vector3(3072, 2048, 0));
        await Assert.That(geometry.GetWaterLevel(new(3074, 2050, 9), 0)).IsEqualTo(10f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0)).IsEqualTo(0f);
    }

    [Test]
    public async Task OceanMarkerAndInvalidRiverCount_DoNotCreatePhysics()
    {
        var geometry = new HousingWaterGeometry();
        geometry.Add(Water(type: WaterObjectVolumeType.Ocean), Vector3.Zero);
        var odd = Water(id: 2, type: WaterObjectVolumeType.River);
        odd.PhysicsContourPointsList.RemoveAt(0);
        geometry.Add(odd, Vector3.Zero);
        await Assert.That(geometry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NonfiniteProbe_FailsClosed()
    {
        var geometry = new HousingWaterGeometry();
        await Assert.That(float.IsNaN(geometry.GetWaterLevel(new(float.NaN, 0, 0), 0))).IsTrue();
    }

    [Test]
    public async Task Load_ReadsObjectThenBigObjectWithNativeIdDedup()
    {
        var first = Water();
        var duplicate = Water(surface: 20);
        var extra = Water(id: 2, surface: 30);
        var files = new Dictionary<string, byte[]>
        {
            ["object.dat"] = ObjectFile(first.Data),
            ["big_object.dat"] = new byte[8].Concat(duplicate.Data).Concat(extra.Data).ToArray()
        };
        var world = new WorldTemplate { Name = "test", Cells = new WorldCell[1, 1] };
        var geometry = HousingWaterGeometry.Load(world, path =>
            files.TryGetValue(Path.GetFileName(path), out var bytes) ? new MemoryStream(bytes) : null);
        await Assert.That(geometry.Count).IsEqualTo(2);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0)).IsEqualTo(10f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 29), 0)).IsEqualTo(30f);
    }

    [Test]
    public async Task ExactClientBigObject_AddsTheMissingPhysicalWaterArea()
    {
        var root = Environment.GetEnvironmentVariable("AAEMU_STATIC_WORLD_CLIENT_ROOT");
        Skip.Unless(!string.IsNullOrEmpty(root), "Set AAEMU_STATIC_WORLD_CLIENT_ROOT to extracted static client files.");
        var cell = Path.Combine(root!, "game/worlds/main_world/cells/020_031/client");
        var objects = new ObjectsFile(Path.Combine(cell, "object.dat"));
        await Assert.That(objects.ReadFile(File.OpenRead(objects.FileName))).IsTrue();
        var offset = new Vector3(20 * 1024, 31 * 1024, 0);
        var geometry = new HousingWaterGeometry();
        foreach (var water in objects.PrefabsList.OfType<ObjectDataType11Water>())
            geometry.Add(water, offset);
        var count = geometry.Count;
        await Assert.That(geometry.GetWaterLevel(new(21000, 32800, 297), 0)).IsEqualTo(0f);
        using var stream = File.OpenRead(Path.Combine(cell, "big_object.dat"));
        var big = CryBigObjectsFile.Read(stream);
        var missing = big.Objects.OfType<ObjectDataType11Water>().Single(water => water.VolumeId == 4639442551196662424UL);
        geometry.Add(missing, offset);
        await Assert.That(geometry.Count).IsEqualTo(count + 1);
        await Assert.That(geometry.GetWaterLevel(new(21000, 32800, 297), 0)).IsEqualTo(298f);
    }

    private static ObjectDataType11Water Water(ulong id = 1, float surface = 10, float size = 4,
        WaterObjectVolumeType type = WaterObjectVolumeType.Area, Vector3? normal = null, bool rawPhysics = false,
        float depth = 5)
    {
        var bytes = new byte[123 + 8 * 12];
        Write(bytes, 0, 11);
        Vector(bytes, 4, new Vector3(0, 0, surface - depth));
        Vector(bytes, 16, new Vector3(size, size, surface));
        bytes[43] = (byte)type;
        BitConverter.TryWriteBytes(bytes.AsSpan(47), id);
        Vector(bytes, 75, normal ?? Vector3.UnitZ);
        Float(bytes, 87, -999); // Loader replaces the serialized distance for Area and River.
        Write(bytes, 107, 4);
        Float(bytes, 111, depth);
        Write(bytes, 119, 4);
        var points = new Vector3[] { new(0, 0, surface), new(size, 0, surface), new(size, size, surface), new(0, size, surface) };
        for (var i = 0; i < 4; i++)
        {
            Vector(bytes, 123 + i * 12, points[i]);
            Vector(bytes, 171 + i * 12, points[i] with { Z = rawPhysics ? i * 100 : surface });
        }
        var water = new ObjectDataType11Water();
        water.ReadData(bytes, 0);
        return water;
    }

    private static byte[] ObjectFile(byte[] record)
    {
        var bytes = new byte[41 + record.Length];
        Write(bytes, 36, record.Length);
        record.CopyTo(bytes, 41);
        return bytes;
    }

    private static void Write(byte[] bytes, int offset, int value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset), value);
    private static void Float(byte[] bytes, int offset, float value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset), value);
    private static void Vector(byte[] bytes, int offset, Vector3 value)
    {
        Float(bytes, offset, value.X); Float(bytes, offset + 4, value.Y); Float(bytes, offset + 8, value.Z);
    }
}
