using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

[NotInParallel]
public class SCInitialConfigPacketTests
{
    [Test]
    public async Task Write_WritesThresholdForTwelveHourIdleKick()
    {
        const ushort expectedThresholdSeconds = (12 * 60 * 60) - (5 * 60);
        new FeaturesManager(Mock.Of<IExperienceManager>().Object).Initialize();

        var stream = new SCInitialConfigPacket().Write(new PacketStream());
        stream.Pos = stream.Count - sizeof(ushort);

        await Assert.That(stream.ReadUInt16()).IsEqualTo(expectedThresholdSeconds);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
