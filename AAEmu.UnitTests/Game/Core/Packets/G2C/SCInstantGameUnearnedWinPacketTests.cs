using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class SCInstantGameUnearnedWinPacketTests
{
    [Test]
    [Arguments(0u)]
    [Arguments(12_345u)]
    [Arguments(uint.MaxValue)]
    public async Task DefaultWinNotice_WritesTheNativeRemainingMilliseconds(uint milliseconds)
    {
        var packet = new SCInstantGameUnearnedWinPacket(milliseconds);
        var body = packet.Write(new PacketStream());
        body.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x196);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.ReadUInt32()).IsEqualTo(milliseconds);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }
}
