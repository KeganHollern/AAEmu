using System.Numerics;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.C2G;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

public sealed class CSCreateShipyardPacketTests
{
    [Test]
    public async Task Body_KeepsExactDesignIdAndAllLocalBounds()
    {
        var stream = Body();
        await Assert.That(stream.Count).IsEqualTo(61);
        var request = CSCreateShipyardPacket.ReadRequest(stream);
        await Assert.That(request.TemplateId).IsEqualTo(15u);
        await Assert.That(request.Position).IsEqualTo(new Vector3(12000.25f, 17000.75f, 109.9f));
        await Assert.That(request.Yaw).IsEqualTo(1.25f);
        await Assert.That(request.DesignItemId).IsEqualTo(0x0123456789abcdefUL);
        await Assert.That(request.LocalBounds.Min).IsEqualTo(new Vector3(-1, -2, -3));
        await Assert.That(request.LocalBounds.Max).IsEqualTo(new Vector3(4, 5, 6));
        await Assert.That(request.AutoUseAAPoint).IsTrue();
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Body_RejectsEveryTruncatedLengthAndTrailingData()
    {
        var bytes = Body().GetBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            var stream = new PacketStream(bytes[..length]);
            await Assert.That(() => CSCreateShipyardPacket.ReadRequest(stream)).Throws<MarshalException>();
            await Assert.That(stream.Pos).IsEqualTo(0);
        }
        await Assert.That(() => CSCreateShipyardPacket.ReadRequest(new PacketStream([.. bytes, 0])))
            .Throws<MarshalException>();
    }

    private static PacketStream Body()
    {
        var stream = new PacketStream();
        stream.Write(15u);
        stream.Write(Helpers.ConvertLongX(12000.25f));
        stream.Write(Helpers.ConvertLongY(17000.75f));
        stream.Write(109.9f);
        stream.Write(1.25f);
        stream.Write(0x0123456789abcdefUL);
        foreach (var value in new[] { -1f, -2f, -3f, 4f, 5f, 6f })
            stream.Write(value);
        stream.Write(true);
        return stream;
    }
}
