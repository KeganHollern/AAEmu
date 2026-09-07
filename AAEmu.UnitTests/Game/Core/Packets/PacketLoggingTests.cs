using System.Net;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Stream;

using NLog;
using NLog.Config;
using NLog.Targets;

namespace AAEmu.UnitTests.Game.Core.Packets;

[NotInParallel]
public class PacketLoggingTests
{
    [Test]
    public async Task PacketBase_DefaultLogLevelIsTrace()
    {
        var gamePacket = new ProbeGamePacket();
        var streamPacket = new ProbeStreamPacket();

        await Assert.That(gamePacket.LogLevel).IsEqualTo(PacketLogLevel.Trace);
        await Assert.That(streamPacket.LogLevel).IsEqualTo(PacketLogLevel.Trace);
    }

    [Test]
    public async Task Packets_DoNotBuildVerboseTextWhenSelectedLevelIsDisabled()
    {
        var originalConfiguration = LogManager.Configuration;
        using var target = new NullTarget();
        try
        {
            LogManager.Configuration = CreateConfiguration(LogLevel.Info, target);

            var gamePacket = new ProbeGamePacket();
            gamePacket.Encode();
            gamePacket.Decode(new PacketStream());

            var streamPacket = new ProbeStreamPacket();
            streamPacket.Encode();
            streamPacket.Decode(new PacketStream());

            await Assert.That(gamePacket.VerboseCallCount).IsEqualTo(0);
            await Assert.That(streamPacket.VerboseCallCount).IsEqualTo(0);
        }
        finally
        {
            LogManager.Configuration = originalConfiguration;
        }
    }

    [Test]
    public async Task OffPackets_DoNotBuildVerboseTextWhenTraceIsEnabled()
    {
        var originalConfiguration = LogManager.Configuration;
        using var target = new NullTarget();
        try
        {
            LogManager.Configuration = CreateConfiguration(LogLevel.Trace, target);

            var gamePacket = new ProbeGamePacket(PacketLogLevel.Off);
            gamePacket.Encode();
            gamePacket.Decode(new PacketStream());

            var streamPacket = new ProbeStreamPacket(PacketLogLevel.Off);
            streamPacket.Encode();
            streamPacket.Decode(new PacketStream());

            await Assert.That(gamePacket.VerboseCallCount).IsEqualTo(0);
            await Assert.That(streamPacket.VerboseCallCount).IsEqualTo(0);
        }
        finally
        {
            LogManager.Configuration = originalConfiguration;
        }
    }

    [Test]
    public async Task Packets_BuildVerboseTextWhenSelectedLevelIsEnabled()
    {
        var originalConfiguration = LogManager.Configuration;
        using var target = new NullTarget();
        try
        {
            LogManager.Configuration = CreateConfiguration(LogLevel.Trace, target);

            var gamePacket = new ProbeGamePacket();
            gamePacket.Encode();
            gamePacket.Decode(new PacketStream());

            var streamPacket = new ProbeStreamPacket();
            streamPacket.Encode();
            streamPacket.Decode(new PacketStream());

            await Assert.That(gamePacket.VerboseCallCount).IsEqualTo(2);
            await Assert.That(streamPacket.VerboseCallCount).IsEqualTo(2);
        }
        finally
        {
            LogManager.Configuration = originalConfiguration;
        }
    }

    [Test]
    public async Task GameUnknownPackets_AreBoundPerConnectionAndKeepFirstContext()
    {
        var originalConfiguration = LogManager.Configuration;
        using var target = CreateRejectedPacketTarget(includePacketLevel: true);
        try
        {
            LogManager.Configuration = CreateConfiguration(LogLevel.Warn, target);

            var handler = new GameProtocolHandler();
            var connection = new GameConnection(CreateSession(71, "10.0.0.1").Object);
            var packet = CreateGamePacket(0x1234, [0xaa, 0xbb]);

            for (var i = 0; i < ConnectionEventLimiter.DefaultLimit + 2; i++)
                handler.OnReceive(connection, packet, 0, packet.Length);

            await Assert.That(target.Logs.Count).IsEqualTo(ConnectionEventLimiter.DefaultLimit);
            await Assert.That(target.Logs[0])
                .IsEqualTo("game.packet.rejected|game|4660|1|2|71|10.0.0.1");

            var freshConnection = new GameConnection(CreateSession(72, "10.0.0.2").Object);
            handler.OnReceive(freshConnection, packet, 0, packet.Length);

            await Assert.That(target.Logs.Count).IsEqualTo(ConnectionEventLimiter.DefaultLimit + 1);
            await Assert.That(target.Logs[^1])
                .IsEqualTo("game.packet.rejected|game|4660|1|2|72|10.0.0.2");
        }
        finally
        {
            LogManager.Configuration = originalConfiguration;
        }
    }

    [Test]
    public async Task StreamUnknownPackets_AreBoundPerConnectionAndKeepFirstContext()
    {
        var originalConfiguration = LogManager.Configuration;
        using var target = CreateRejectedPacketTarget(includePacketLevel: false);
        try
        {
            LogManager.Configuration = CreateConfiguration(LogLevel.Warn, target);

            var handler = new StreamProtocolHandler();
            var connection = new StreamConnection(CreateSession(81, "10.0.1.1").Object);
            var packet = CreateStreamPacket(0x2345, [0xcc, 0xdd, 0xee]);

            for (var i = 0; i < ConnectionEventLimiter.DefaultLimit + 2; i++)
                handler.OnReceive(connection, packet, 0, packet.Length);

            await Assert.That(target.Logs.Count).IsEqualTo(ConnectionEventLimiter.DefaultLimit);
            await Assert.That(target.Logs[0])
                .IsEqualTo("game.packet.rejected|stream|9029|3|81|10.0.1.1");

            var freshConnection = new StreamConnection(CreateSession(82, "10.0.1.2").Object);
            handler.OnReceive(freshConnection, packet, 0, packet.Length);

            await Assert.That(target.Logs.Count).IsEqualTo(ConnectionEventLimiter.DefaultLimit + 1);
            await Assert.That(target.Logs[^1])
                .IsEqualTo("game.packet.rejected|stream|9029|3|82|10.0.1.2");
        }
        finally
        {
            LogManager.Configuration = originalConfiguration;
        }
    }

    private static LoggingConfiguration CreateConfiguration(LogLevel minimumLevel, Target target)
    {
        var configuration = new LoggingConfiguration();
        configuration.AddRule(minimumLevel, LogLevel.Fatal, target);
        return configuration;
    }

    private static MemoryTarget CreateRejectedPacketTarget(bool includePacketLevel)
    {
        var packetLevel = includePacketLevel ? "${event-properties:item=PacketLevel}|" : string.Empty;
        return new MemoryTarget
        {
            Layout = "${event-properties:item=EventName}|${event-properties:item=Network}|" +
                     "${event-properties:item=PacketOpcode}|" + packetLevel +
                     "${event-properties:item=PacketLength}|${event-properties:item=ConnectionId}|" +
                     "${event-properties:item=RemoteIp}"
        };
    }

    private static Mock<ISession> CreateSession(uint sessionId, string ip)
    {
        var session = Mock.Of<ISession>();
        session.SessionId.Returns(sessionId);
        session.Ip.Returns(IPAddress.Parse(ip));
        return session;
    }

    private static byte[] CreateGamePacket(ushort opcode, byte[] payload)
    {
        var body = new PacketStream()
            .Write((byte)0)
            .Write((byte)1)
            .Write((byte)0)
            .Write((byte)0)
            .Write(opcode)
            .Write(payload);
        return new PacketStream().Write(body).GetBytes();
    }

    private static byte[] CreateStreamPacket(ushort opcode, byte[] payload)
    {
        var body = new PacketStream()
            .Write(opcode)
            .Write(payload);
        return new PacketStream().Write(body).GetBytes();
    }

    private sealed class ProbeGamePacket(PacketLogLevel? logLevel = null) : GamePacket(0x123, 1)
    {
        public int VerboseCallCount { get; private set; }

        public override PacketLogLevel LogLevel => logLevel ?? base.LogLevel;

        public override void Read(PacketStream stream) { }

        public override PacketStream Write(PacketStream stream) => stream;

        public override string Verbose()
        {
            VerboseCallCount++;
            return " probe";
        }
    }

    private sealed class ProbeStreamPacket(PacketLogLevel? logLevel = null) : StreamPacket(0x123)
    {
        public int VerboseCallCount { get; private set; }

        public override PacketLogLevel LogLevel => logLevel ?? base.LogLevel;

        public override void Read(PacketStream stream) { }

        public override PacketStream Write(PacketStream stream) => stream;

        public override string Verbose()
        {
            VerboseCallCount++;
            return " probe";
        }
    }
}
