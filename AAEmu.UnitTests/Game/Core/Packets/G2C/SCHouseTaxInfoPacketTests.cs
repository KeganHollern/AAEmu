using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class SCHouseTaxInfoPacketTests
{
    [Test]
    [Arguments(1_795_877_782L, 0, true, false)]
    [Arguments(1_796_482_582L, 1, false, true)]
    public async Task Write_HouseTaxInfo_UsesNativeFieldOrder(long statusUnixTime, int weeksWithoutPay, bool paid, bool heavyTax)
    {
        var statusDate = DateTimeOffset.FromUnixTimeSeconds(statusUnixTime).UtcDateTime;
        var packet = new SCHouseTaxInfoPacket(42, 7, 200_000, 100_000, statusDate, paid, weeksWithoutPay, heavyTax);
        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0xc4);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(stream.Count).IsEqualTo(28);
        await Assert.That(stream.ReadUInt16()).IsEqualTo((ushort)42);
        await Assert.That(stream.ReadInt32()).IsEqualTo(7);
        await Assert.That(stream.ReadInt32()).IsEqualTo(200_000);
        await Assert.That(stream.ReadInt32()).IsEqualTo(100_000);
        await Assert.That(stream.ReadInt64()).IsEqualTo(statusUnixTime);
        await Assert.That(stream.ReadInt32()).IsEqualTo(weeksWithoutPay);
        await Assert.That(stream.ReadBoolean()).IsEqualTo(paid);
        await Assert.That(stream.ReadBoolean()).IsEqualTo(heavyTax);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
