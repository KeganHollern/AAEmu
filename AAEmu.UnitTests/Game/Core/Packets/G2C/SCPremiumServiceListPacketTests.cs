using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Features;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCPremiumServiceListPacketTests
{
    [Test]
    public async Task DisabledService_DoesNotAdvertisePremiumPurchases()
    {
        await Assert.That(new FeatureSet().Check(Feature.premium)).IsFalse();
    }

    [Test]
    public async Task DisabledService_EmitsCompleteEmptyList()
    {
        var stream = new PacketStream();
        new SCPremiumServiceListPacket().Write(stream);
        await Assert.That(stream.ReadBoolean()).IsTrue();
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)0);
        await Assert.That(stream.ReadInt32()).IsEqualTo(0); // cid
        await Assert.That(stream.ReadString()).IsEqualTo(string.Empty);
        await Assert.That(stream.ReadUInt16()).IsEqualTo((ushort)0);
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)0); // isSell
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)0); // isHidden
        await Assert.That(stream.ReadInt32()).IsEqualTo(0); // ptime
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)0); // ptype
        await Assert.That(stream.ReadInt32()).IsEqualTo(0); // price
        await Assert.That(stream.ReadUInt32()).IsEqualTo(0u); // id
        await Assert.That(stream.ReadInt32()).IsEqualTo(0); // bcount
        await Assert.That(stream.ReadInt32()).IsEqualTo(0); // exchangeRatio
        await Assert.That(stream.Pos).IsEqualTo(stream.Count);
    }
}
