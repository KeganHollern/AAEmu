using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCTimeOfDayPacketTests
{
    [Test]
    public async Task DetailedPacket_WritesR208022ClockLayout()
    {
        var packet = new SCDetailedTimeOfDayPacket(12.5f, 1f / 3600f, 0f, 24f);
        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCDetailedTimeOfDayPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(stream.Count).IsEqualTo(16);
        await Assert.That(stream.ReadSingle()).IsEqualTo(12.5f);
        await Assert.That(stream.ReadSingle()).IsEqualTo(1f / 3600f);
        await Assert.That(stream.ReadSingle()).IsEqualTo(0f);
        await Assert.That(stream.ReadSingle()).IsEqualTo(24f);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task SimplePacket_WritesR208022ClockLayout()
    {
        var packet = new SCTimeOfDayPacket(12.5f);
        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCTimeOfDayPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(stream.Count).IsEqualTo(4);
        await Assert.That(stream.ReadSingle()).IsEqualTo(12.5f);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
