using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCMailReceiptPacketTests
{
    [Test]
    public async Task ReceiverOpened_Write_WritesR208022Layout()
    {
        const long mailId = 0x0102030405060708;
        var openDate = new DateTime(2026, 8, 29, 12, 34, 56, DateTimeKind.Utc);
        var packet = new SCMailReceiverOpenedPacket(mailId, openDate);

        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCMailReceiverOpenedPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadInt64()).IsEqualTo(mailId);
        await Assert.That(stream.ReadInt64()).IsEqualTo(Helpers.UnixTime(openDate));
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Removed_Write_WritesR208022SentMailLayout()
    {
        const long mailId = 0x0102030405060708;
        var packet = new SCMailRemovedPacket(true, mailId);

        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCMailRemovedPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBoolean()).IsTrue();
        await Assert.That(stream.ReadInt64()).IsEqualTo(mailId);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
