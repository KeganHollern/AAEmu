using AAEmu.Game.Models.ClientData;

namespace AAEmu.UnitTests.Game.Models.ClientData;

public class NodeCellTests
{
    private const float HeightStep = 0.05f;

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(9)]
    [Arguments(17)]
    [Arguments(33)]
    public async Task Read_VaryingResolution_PreservesNativeGrid(int size)
    {
        var node = ReadNode(size, (x, y) => EncodeHeight(x * 100 + y * 10, (x + y) & 0xF));

        await Assert.That(node.nSize).IsEqualTo(size);
        await Assert.That(node.pHMData.Length).IsEqualTo(size * size);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(9)]
    [Arguments(17)]
    [Arguments(33)]
    public async Task GetHeightAtUnit_VaryingResolution_InterpolatesNativeHeightGrid(int size)
    {
        var node = ReadNode(size, (x, y) => EncodeHeight(x * 100 + y * 10, (x + y) & 0xF));
        var expectedMidpoint = (size - 1) * 55 * HeightStep;
        var expectedEndpoint = (size - 1) * 110 * HeightStep;

        await Assert.That(node.GetHeightAtUnit(16, 16)).IsEqualTo(expectedMidpoint).Within(0.001f);
        await Assert.That(node.GetHeightAtUnit(32, 32)).IsEqualTo(expectedEndpoint).Within(0.001f);
    }

    [Test]
    public async Task GetHeightAtUnit_DifferentPackedFlags_ReturnsSameHeight()
    {
        var clearFlags = ReadNode(5, (x, y) => EncodeHeight(x * 100 + y * 10, 0));
        var variedFlags = ReadNode(5, (x, y) => EncodeHeight(x * 100 + y * 10, (x * 5 + y * 3) & 0xF));
        var sampleUnits = new ushort[] { 0, 7, 16, 29, 32 };

        foreach (var unitX in sampleUnits)
        foreach (var unitY in sampleUnits)
        {
            await Assert.That(variedFlags.GetHeightAtUnit(unitX, unitY))
                .IsEqualTo(clearFlags.GetHeightAtUnit(unitX, unitY))
                .Within(0.001f);
        }
    }

    [Test]
    public async Task GetHeightAtUnit_AsymmetricGrid_InterpolatesCorrectCorners()
    {
        var heightCodes = new[,]
        {
            { 100, 700 },
            { 300, 1500 }
        };
        var node = ReadNode(2, (x, y) => EncodeHeight(heightCodes[x, y], (x * 5 + y * 3) & 0xF));
        const float ExpectedHeightCode = 712.5f;

        await Assert.That(node.GetHeightAtUnit(8, 24))
            .IsEqualTo(ExpectedHeightCode * HeightStep)
            .Within(0.001f);
    }

    [Test]
    public async Task Read_HoleMaskUsesAllFiveOriginalSurfaceBitsBeforeHeightRescaling()
    {
        var node = ReadNode(3, (x, y) => (ushort)(x == 0 && y == 1 ? 31 : 15), 5, true);
        await Assert.That(node.TerrainHoles[15 * 32 + 16]).IsTrue();
        await Assert.That(node.TerrainHoles[15 * 32 + 15]).IsFalse();
        await Assert.That(node.TerrainHoles[16 * 32 + 16]).IsFalse();
        var solid = ReadNode(33, (_, _) => 15, 5, true);
        await Assert.That(solid.TerrainHoles).IsNull();
    }

    [Test]
    [Arguments(2, 0.037f)]
    [Arguments(17, 0.0041f)]
    [Arguments(33, 0.0041f)]
    [Arguments(33, 0f)]
    public async Task TerrainSector_PreservesBothHeightsAndAllOriginalSurfaceBits(int size, float range)
    {
        const float offset = 100.038f;
        const double coefficient = 65.535;
        ushort Packed(int x, int y) => (ushort)(((x + 3 * y) << 5) | ((x + y) % 2 == 0 ? 16 : 0));
        var original = ReadNode(size, Packed, 5, offset: offset, range: range, readOriginal: true);
        var legacy = ReadNode(size, Packed, 5, offset: offset, range: range);
        var sector = new TerrainHeightSector(original, coefficient, 33, 33, true, out _, out _);
        foreach (var x in new ushort[] { 0, 7, 32 })
        foreach (var y in new ushort[] { 0, 19, 32 })
        {
            var expected = offset + ((x + 3 * y) * (size - 1)) * range;
            await Assert.That(sector.NativeHeight(x, y)).IsEqualTo(expected).Within(0.0001f);
            await Assert.That(sector.LegacyHeight(x, y, coefficient))
                .IsEqualTo((ushort)(legacy.GetHeightAtUnit(x, y) * coefficient));
        }
    }

    private static ushort EncodeHeight(int height, int flags)
    {
        return (ushort)((height << 4) | (flags & 0xF));
    }

    internal static NodeCell ReadNode(int size, Func<int, int, ushort> createRawHeight, byte version = 7,
        bool hasHoles = false, float offset = 0f, float range = 0.003125f, bool readOriginal = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(version); // Version
            writer.Write((byte)0); // Dummy
            writer.Write((byte)0); // Flags
            writer.Write((byte)0); // Flags2
            for (var i = 0; i < 6; i++)
                writer.Write(0f); // BoxHeightmap
            writer.Write((byte)(hasHoles ? 1 : 0)); // bHasHoles
            writer.Write(offset); // fOffset
            writer.Write(range); // fRange
            writer.Write(size);
            writer.Write(0); // unknown data length
            for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
                writer.Write(createRawHeight(x, y));
            writer.Write(0);
            for (var i = 0; i < 4; i++)
                writer.Write(0f);
            writer.Write(new byte[36]);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var node = new NodeCell();
        node.Read(reader, readOriginal);
        return node;
    }
}
