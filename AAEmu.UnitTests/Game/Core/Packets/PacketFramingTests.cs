using System.Net;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Network.Stream;

namespace AAEmu.UnitTests.Game.Core.Packets;

public class PacketFramingTests
{
    private const ushort ProbeOpcode = 0x1234;
    private const uint PayloadValue = 0x78563412;

    [Test]
    [Arguments("game")]
    [Arguments("stream")]
    [Arguments("login")]
    public async Task OnReceive_SplitHeader_RetainsAndReassemblesPacket(string network)
    {
        var probe = CreateProtocol(network);
        var frame = CreateFrame(network, new PacketStream().Write(PayloadValue).GetBytes());

        await ReceiveAsync(probe, frame, 0, 1);

        await Assert.That(probe.LastPacket()?.GetBytes()).IsEquivalentTo(frame[..1]);
        probe.Session.Close().WasCalled(Times.Never);
        probe.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);

        await ReceiveAsync(probe, frame, 1, 1);

        await Assert.That(probe.LastPacket()?.GetBytes()).IsEquivalentTo(frame[..2]);
        probe.Session.Close().WasCalled(Times.Never);
        probe.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);

        await ReceiveAsync(probe, frame, 2, frame.Length - 2);

        await AssertDecodedReply(probe, PayloadValue);
    }

    [Test]
    [Arguments("game")]
    [Arguments("stream")]
    [Arguments("login")]
    public async Task OnReceive_SplitPayload_RetainsAndReassemblesPacket(string network)
    {
        var probe = CreateProtocol(network);
        var frame = CreateFrame(network, new PacketStream().Write(PayloadValue).GetBytes());

        await ReceiveAsync(probe, frame, 0, frame.Length - 1);

        await Assert.That(probe.LastPacket()?.GetBytes()).IsEquivalentTo(frame[..^1]);
        probe.Session.Close().WasCalled(Times.Never);
        probe.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);

        await ReceiveAsync(probe, frame, frame.Length - 1, 1);

        await AssertDecodedReply(probe, PayloadValue);
    }

    [Test]
    [Arguments("game")]
    [Arguments("stream")]
    [Arguments("login")]
    public async Task OnReceive_CompletePacket_DecodesOriginalPayload(string network)
    {
        var probe = CreateProtocol(network);
        var frame = CreateFrame(network, new PacketStream().Write(PayloadValue).GetBytes());

        await ReceiveAsync(probe, frame, 0, frame.Length);

        await AssertDecodedReply(probe, PayloadValue);
    }

    [Test]
    [Arguments("game")]
    [Arguments("stream")]
    [Arguments("login")]
    public async Task OnReceive_CompleteFrameWithShortPayload_ClosesWithoutDispatchingDefaultValue(string network)
    {
        var probe = CreateProtocol(network);
        var frame = CreateFrame(network, [0x12]);

        await ReceiveAsync(probe, frame, 0, frame.Length);

        probe.Session.Close().WasCalled(Times.Once);
        probe.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
        await Assert.That(probe.LastPacket()).IsNull();
    }

    [Test]
    [Arguments("game")]
    [Arguments("stream")]
    [Arguments("login")]
    public async Task OnReceive_CompletePacketThenSplitHeader_RetainsOnlyNextPacket(string network)
    {
        var probe = CreateProtocol(network);
        var firstFrame = CreateFrame(network, new PacketStream().Write(PayloadValue).GetBytes());
        var secondFrame = CreateFrame(network, new PacketStream().Write(PayloadValue + 1).GetBytes());
        byte[] firstReceive = [.. firstFrame, secondFrame[0]];

        await ReceiveAsync(probe, firstReceive, 0, firstReceive.Length);

        await Assert.That(probe.LastPacket()?.GetBytes()).IsEquivalentTo(secondFrame[..1]);
        var firstReply = probe.Reply(PayloadValue);
        probe.Session.SendPacket(Is<byte[]>(actual => actual.SequenceEqual(firstReply))).WasCalled(Times.Once);
        probe.Session.Close().WasCalled(Times.Never);

        await ReceiveAsync(probe, secondFrame, 1, secondFrame.Length - 1);

        await Assert.That(probe.LastPacket()).IsNull();
        var secondReply = probe.Reply(PayloadValue + 1);
        probe.Session.SendPacket(Is<byte[]>(actual => actual.SequenceEqual(secondReply))).WasCalled(Times.Once);
        probe.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Exactly(2));
        probe.Session.Close().WasCalled(Times.Never);
    }

    private static async Task AssertDecodedReply(ProtocolProbe probe, uint value)
    {
        await Assert.That(probe.LastPacket()).IsNull();
        var reply = probe.Reply(value);
        probe.Session.SendPacket(Is<byte[]>(actual => actual.SequenceEqual(reply))).WasCalled(Times.Once);
        probe.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Once);
        probe.Session.Close().WasCalled(Times.Never);
    }

    private static async Task ReceiveAsync(ProtocolProbe probe, byte[] bytes, int offset, int count)
    {
        // A regression in frame advancement must fail the test within a fixed time.
        await Task.Run(() => probe.Receive(bytes, offset, count)).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static ProtocolProbe CreateProtocol(string network)
    {
        var session = Mock.Of<ISession>();
        session.SessionId.Returns(91u);
        session.Ip.Returns(IPAddress.Parse("192.0.2.9"));

        switch (network)
        {
            case "game":
            {
                var connection = new GameConnection(session.Object);
                connection.TryAuthenticate(101);
                var handler = new GameProtocolHandler();
                handler.RegisterPacket(ProbeOpcode, 1, typeof(ProbeGamePacket));
                return new ProtocolProbe(session,
                    (bytes, offset, count) => handler.OnReceive(connection, bytes, offset, count),
                    () => connection.LastPacket,
                    value => new ProbeGamePacket { Value = value }.Encode().GetBytes());
            }
            case "stream":
            {
                var connection = new StreamConnection(session.Object);
                var handler = new StreamProtocolHandler();
                handler.RegisterPacket(ProbeOpcode, typeof(ProbeStreamPacket));
                return new ProtocolProbe(session,
                    (bytes, offset, count) => handler.OnReceive(connection, bytes, offset, count),
                    () => connection.LastPacket,
                    value => new ProbeStreamPacket { Value = value }.Encode().GetBytes());
            }
            case "login":
            {
                var connection = new LoginConnection(session.Object);
                var handler = new LoginProtocolHandler();
                handler.RegisterPacket(ProbeOpcode, typeof(ProbeLoginPacket));
                return new ProtocolProbe(session,
                    (bytes, offset, count) => handler.OnReceive(connection, bytes, offset, count),
                    () => connection.LastPacket,
                    value => new ProbeLoginPacket { Value = value }.Encode().GetBytes());
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(network), network, "Unknown network.");
        }
    }

    private static byte[] CreateFrame(string network, byte[] payload)
    {
        var body = new PacketStream();
        if (network == "game")
            body.Write((byte)0).Write((byte)1).Write((byte)0).Write((byte)0);
        body.Write(ProbeOpcode).Write(payload);
        return new PacketStream().Write(body).GetBytes();
    }

    private sealed record ProtocolProbe(
        Mock<ISession> Session,
        Action<byte[], int, int> Receive,
        Func<PacketStream> LastPacket,
        Func<uint, byte[]> Reply);

    public sealed class ProbeGamePacket() : GamePacket(ProbeOpcode, 1)
    {
        public uint Value { get; set; }
        public override PacketLogLevel LogLevel => PacketLogLevel.Off;

        public override void Read(PacketStream stream)
        {
            Value = stream.ReadUInt32();
            Connection.SendPacket(this);
        }

        public override PacketStream Write(PacketStream stream) => stream.Write(Value);
    }

    public sealed class ProbeStreamPacket() : StreamPacket(ProbeOpcode)
    {
        public uint Value { get; set; }
        public override PacketLogLevel LogLevel => PacketLogLevel.Off;

        public override void Read(PacketStream stream)
        {
            Value = stream.ReadUInt32();
            Connection.SendPacket(this);
        }

        public override PacketStream Write(PacketStream stream) => stream.Write(Value);
    }

    public sealed class ProbeLoginPacket() : LoginPacket(ProbeOpcode)
    {
        public uint Value { get; set; }

        public override void Read(PacketStream stream)
        {
            Value = stream.ReadUInt32();
            Connection.SendPacket(this);
        }

        public override PacketStream Write(PacketStream stream) => stream.Write(Value);
    }
}
