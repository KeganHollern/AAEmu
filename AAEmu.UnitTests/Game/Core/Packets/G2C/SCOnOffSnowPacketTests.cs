using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCOnOffSnowPacketTests
{
    [Test]
    [Arguments(false, (byte)0)]
    [Arguments(true, (byte)1)]
    public async Task Write_WritesR208022SnowLayout(bool on, byte expectedValue)
    {
        var packet = new SCOnOffSnowPacket(on);
        var body = packet.Write(new PacketStream());
        body.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x00B4);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.Count).IsEqualTo(1);
        await Assert.That(body.ReadByte()).IsEqualTo(expectedValue);
        await Assert.That(body.LeftBytes).IsEqualTo(0);

        var frame = packet.Encode().GetBytes();
        byte[] expectedFrame = [0x07, 0x00, 0xDD, 0x01, 0x00, 0x00, 0xB4, 0x00, expectedValue];
        await Assert.That(frame.SequenceEqual(expectedFrame)).IsTrue();
    }
}
