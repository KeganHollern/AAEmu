using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Readers;

namespace AAEmu.UnitTests.Game.Models.CryEngine.Readers;

public class NetMissionReaderTests
{
    [Test]
    public async Task ReadFile_NodeNavigationMetadata_DecodesSerializedLayout()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(NetMissionReader.BaiTriangulationFileVersion);
            WriteVector3(writer, Vector3.Zero); // Bounding-box minimum
            WriteVector3(writer, Vector3.One); // Bounding-box maximum
            writer.Write(1); // Node count
            writer.Write(42); // Node ID
            WriteVector3(writer, Vector3.UnitX); // Direction
            WriteVector3(writer, Vector3.UnitZ); // Up
            WriteVector3(writer, new Vector3(1f, 2f, 3f)); // Position
            writer.Write(7); // Node index
            writer.Write(11); // Obstacle index 0
            writer.Write(12); // Obstacle index 1
            writer.Write(13); // Obstacle index 2
            writer.Write((ushort)0x0204); // Navigation type
            writer.Write((byte)0xA5); // Packed flags
            writer.Write((byte)0x5A); // Padding
            writer.Write(0); // Edge count
        }

        stream.Position = 0;
        var reader = new NetMissionReader(stream, 1);

        reader.ReadFile();

        var node = reader.NodeDescriptorList[42];
        await Assert.That(node.NavigationType).IsEqualTo((BaiNavigationType)0x0204);
        await Assert.That(node.Flags).IsEqualTo((byte)0xA5);
        await Assert.That(node.Padding).IsEqualTo((byte)0x5A);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
}
