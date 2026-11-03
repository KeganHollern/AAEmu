using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2L;

namespace AAEmu.UnitTests.Game.Core.Packets.G2L;

public class GLRegisterGameServerPacketTests
{
    [Test]
    [Arguments((byte)1)]
    [Arguments(byte.MaxValue)]
    public async Task Write_OneServerId_WritesZeroReservedCount(byte serverId)
    {
        var packet = new GLRegisterGameServerPacket("key", serverId);
        var body = packet.Write(new PacketStream());
        body.Rollback();

        await Assert.That(body.ReadString()).IsEqualTo("key");
        await Assert.That(body.ReadByte()).IsEqualTo(serverId);
        await Assert.That(body.ReadInt32()).IsEqualTo(0);
        await Assert.That(body.LeftBytes).IsEqualTo(0);

        // Matches the Go Login registration fixture, including the frame and opcode.
        byte[] expectedFrame = [0x0C, 0x00, 0x00, 0x00, 0x03, 0x00,
            (byte)'k', (byte)'e', (byte)'y', serverId, 0x00, 0x00, 0x00, 0x00];
        await Assert.That(packet.Encode().GetBytes().SequenceEqual(expectedFrame)).IsTrue();
    }
}
