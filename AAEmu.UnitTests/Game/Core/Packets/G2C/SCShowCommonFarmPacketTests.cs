using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class SCShowCommonFarmPacketTests
{
    [Test]
    public async Task Write_EmptyList_WritesBothHeaderFields()
    {
        var stream = new SCShowCommonFarmPacket(17, []).Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.ReadUInt32()).IsEqualTo(17u);
        await Assert.That(stream.ReadInt32()).IsEqualTo(0);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Write_Positions_UsesNativeNineBytePackedPositions()
    {
        Vector3[] positions = [new(12345, 23456, 12), new(23450, 12340, 96)];
        var stream = new SCShowCommonFarmPacket(9, positions).Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.ReadUInt32()).IsEqualTo(9u);
        await Assert.That(stream.ReadInt32()).IsEqualTo(2);
        await Assert.That(stream.LeftBytes).IsEqualTo(18);
        foreach (var expected in positions)
        {
            var actual = stream.ReadPosition();
            await Assert.That(Math.Abs(actual.x - expected.X)).IsLessThan(0.01f);
            await Assert.That(Math.Abs(actual.y - expected.Y)).IsLessThan(0.01f);
            await Assert.That(Math.Abs(actual.z - expected.Z)).IsLessThan(0.01f);
        }
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Write_TooManyPositions_LimitsTheCountAndBodyTogether()
    {
        var stream = new SCShowCommonFarmPacket(1, Enumerable.Repeat(Vector3.Zero, 129)).Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(stream.ReadInt32()).IsEqualTo(128);
        for (var i = 0; i < 128; i++)
            stream.ReadPosition();
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
