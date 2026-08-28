using AAEmu.Commons.Network;
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

    private static LoggingConfiguration CreateConfiguration(LogLevel minimumLevel, Target target)
    {
        var configuration = new LoggingConfiguration();
        configuration.AddRule(minimumLevel, LogLevel.Fatal, target);
        return configuration;
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
