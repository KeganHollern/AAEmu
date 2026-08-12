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

    private static ushort EncodeHeight(int height, int flags)
    {
        return (ushort)((height << 4) | (flags & 0xF));
    }

    private static NodeCell ReadNode(int size, Func<int, int, ushort> createRawHeight)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)7); // Version
            writer.Write((byte)0); // Dummy
            writer.Write((byte)0); // Flags
            writer.Write((byte)0); // Flags2
            for (var i = 0; i < 6; i++)
                writer.Write(0f); // BoxHeightmap
            writer.Write((byte)0); // bHasHoles
            writer.Write(0f); // fOffset
            writer.Write(0.003125f); // fRange; produces a five-centimetre integer step
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
        node.Read(reader);
        return node;
    }
}
