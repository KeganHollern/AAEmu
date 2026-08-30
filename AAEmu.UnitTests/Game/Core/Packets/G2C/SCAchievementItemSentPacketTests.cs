using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCAchievementItemSentPacketTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Write_UsesR208022AchievementMailAndItemFields(bool byMail)
    {
        var stream = new SCAchievementItemSentPacket(1478, byMail, 32750).Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadUInt32()).IsEqualTo(1478u);
        await Assert.That(stream.ReadBoolean()).IsEqualTo(byMail);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(32750u);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
