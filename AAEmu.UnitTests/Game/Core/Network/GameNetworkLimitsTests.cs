using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Network;

public sealed class GameNetworkLimitsTests
{
    [Test]
    public async Task Admission_TotalCapRejectsDifferentAddressesAndReleaseRestoresSlot()
    {
        var table = new GameConnectionTable(new GameNetworkLimitsConfig { MaxConnections = 2 });
        var first = Connection(1, "192.0.2.1");
        var second = Connection(2, "192.0.2.2");
        var third = Connection(3, "192.0.2.3");
        await Assert.That(table.AddConnection(first)).IsTrue();
        await Assert.That(table.AddConnection(second)).IsTrue();
        await Assert.That(table.AddConnection(third)).IsFalse();
        await Assert.That(third.IsClosed).IsTrue();
        table.RemoveConnection(first.Session);
        await Assert.That(table.AddConnection(Connection(4, "192.0.2.4"))).IsTrue();
        await Assert.That(table.GetConnections().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Admission_NormalizesMappedIpv4AndDoesNotReleaseRejectedIdentity()
    {
        var table = new GameConnectionTable(new GameNetworkLimitsConfig { MaxConnectionsPerAddress = 1 });
        var first = Connection(1, "192.0.2.1");
        var rejected = Connection(2, "::ffff:192.0.2.1");
        table.AddConnection(first);
        await Assert.That(table.AddConnection(rejected)).IsFalse();
        table.RemoveConnection(rejected.Session);
        await Assert.That(table.AddConnection(Connection(3, "192.0.2.1"))).IsFalse();
        await Assert.That(table.AddConnection(Connection(4, "2001:db8::1"))).IsTrue();
        table.RemoveConnection(first.Session);
        await Assert.That(table.AddConnection(Connection(5, "::ffff:192.0.2.1"))).IsTrue();
    }

    [Test]
    public async Task Admission_ConcurrentSocketsCannotExceedAddressCap()
    {
        var table = new GameConnectionTable(new GameNetworkLimitsConfig { MaxConnectionsPerAddress = 16 });
        var accepted = 0;
        Parallel.For(1, 101, id =>
        {
            if (table.AddConnection(Connection((uint)id, "192.0.2.1")))
                Interlocked.Increment(ref accepted);
        });
        await Assert.That(accepted).IsEqualTo(16);
        await Assert.That(table.GetConnections().Count).IsEqualTo(16);
    }

    [Test]
    public async Task RateLimit_AllCompleteFramesInOneReceiveCountAndClosedSessionStopsDispatch()
    {
        var session = new RecordingSession(1, IPAddress.Loopback);
        var connection = new GameConnection(session, new GameNetworkLimitsConfig { PacketsPerSecond = 3 });
        connection.TryAuthenticate(1);
        var handler = new GameProtocolHandler();
        handler.RegisterPacket(CSOffsets.CSRequestUIDataPacket, 1, typeof(ProbePacket));
        var frame = Frame(CSOffsets.CSRequestUIDataPacket);
        var data = Enumerable.Range(0, 4).SelectMany(_ => frame).ToArray();
        handler.OnReceive(connection, data, 0, data.Length);
        handler.OnReceive(connection, frame, 0, frame.Length);
        await Assert.That(session.Decoded).IsEqualTo(3);
        await Assert.That(session.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task RateLimit_FragmentedFrameCountsOnceAndMonotonicWindowResets()
    {
        var clock = new FakeTimeProvider();
        var session = new RecordingSession(1, IPAddress.Loopback);
        var connection = new GameConnection(session, new GameNetworkLimitsConfig { PacketsPerSecond = 1 }, clock);
        connection.TryAuthenticate(1);
        var handler = new GameProtocolHandler();
        handler.RegisterPacket(CSOffsets.CSRequestUIDataPacket, 1, typeof(ProbePacket));
        var frame = Frame(CSOffsets.CSRequestUIDataPacket);
        for (var index = 0; index < frame.Length; ++index)
            handler.OnReceive(connection, frame, index, 1);
        await Assert.That(session.Decoded).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        handler.OnReceive(connection, frame, 0, frame.Length);
        await Assert.That(session.Decoded).IsEqualTo(2);
        await Assert.That(session.Closes).IsEqualTo(0);
    }

    [Test]
    public async Task RateLimit_UnknownPacketsConsumeBudgetAndLogsRemainBounded()
    {
        var session = new RecordingSession(1, IPAddress.Loopback);
        var connection = new GameConnection(session, new GameNetworkLimitsConfig { PacketsPerSecond = 4 });
        connection.TryAuthenticate(1);
        var handler = new GameProtocolHandler();
        var frame = Frame(CSOffsets.CSRequestUIDataPacket);
        for (var index = 0; index < 5; ++index)
            handler.OnReceive(connection, frame, 0, frame.Length);
        await Assert.That(connection.UnknownPacketEvents.TryConsume()).IsFalse();
        await Assert.That(session.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task RateLimit_DefaultAllowsLargeLegitimateBurst()
    {
        var limit = new GameNetworkLimitsConfig();
        var limiter = new GamePacketRateLimiter(limit.PacketsPerSecond, new FakeTimeProvider());
        for (var index = 0; index < 1000; ++index)
            await Assert.That(limiter.TryConsume()).IsTrue();
        await Assert.That(limiter.TryConsume()).IsFalse();
    }

    [Test]
    public async Task InvalidLimits_RejectStartupInsteadOfDisablingProtection()
    {
        await Assert.That(() => new GameConnectionTable(new GameNetworkLimitsConfig { MaxConnections = 0 })).Throws<InvalidOperationException>();
        await Assert.That(() => new GameConnectionTable(new GameNetworkLimitsConfig { MaxConnectionsPerAddress = -1 })).Throws<InvalidOperationException>();
        await Assert.That(() => new GameConnectionTable(new GameNetworkLimitsConfig { AuthenticationTimeoutSeconds = 0 })).Throws<InvalidOperationException>();
        await Assert.That(() => new GameConnectionTable(new GameNetworkLimitsConfig { PacketsPerSecond = 0 })).Throws<InvalidOperationException>();
    }

    private static GameConnection Connection(uint id, string ip) => new(new RecordingSession(id, IPAddress.Parse(ip)));
    private static byte[] Frame(ushort opcode) => new PacketStream().Write(new PacketStream()
        .Write((byte)0).Write((byte)1).Write((byte)0).Write((byte)0).Write(opcode)).GetBytes();

    public sealed class ProbePacket() : GamePacket(CSOffsets.CSRequestUIDataPacket, 1)
    {
        public override void Read(PacketStream stream) => Connection.AddAttribute("decoded", true);
    }

    private sealed class RecordingSession(uint id, IPAddress ip) : ISession
    {
        public int Decoded { get; private set; }
        public int Closes { get; private set; }
        public IPAddress Ip => ip;
        public uint SessionId => id;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object value) { if (name == "decoded") ++Decoded; }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() => ++Closes;
    }
}
