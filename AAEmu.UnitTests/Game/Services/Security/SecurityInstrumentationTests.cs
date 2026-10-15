using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Network.Stream;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Services.Security;
using AAEmu.Game.Utils.Scripts;
using Microsoft.Extensions.Time.Testing;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace AAEmu.UnitTests.Game.Services.Security;

[NotInParallel]
public sealed class SecurityInstrumentationTests
{
    [Test]
    public async Task UnknownPackets_CountEveryRejectionAfterEventBudgetsAreExhausted()
    {
        var metrics = new GameSecurityMetrics();
        var original = LogManager.Configuration;
        using var target = CaptureEvents();
        try
        {
            var payload = Encoding.UTF8.GetBytes("private-payload-should-never-appear");
            var game = new GameConnection(new Session(1), securityMetrics: metrics);
            game.TryAuthenticate(10);
            game.State = GameState.World;
            game.ActiveChar = new Character(new UnitCustomModelParams()) { AccountId = 10 };
            var stream = new StreamConnection(new Session(2), metrics);
            var login = new LoginConnection(new Session(3), securityMetrics: metrics);
            var gameFrame = GameFrame(0x1234, payload);
            var otherFrame = StreamFrame(0x2345, payload);
            var gameHandler = new GameProtocolHandler();
            var streamHandler = new StreamProtocolHandler();
            var loginHandler = new LoginProtocolHandler();

            for (var index = 0; index < 5; ++index)
            {
                gameHandler.OnReceive(game, gameFrame, 0, gameFrame.Length);
                streamHandler.OnReceive(stream, otherFrame, 0, otherFrame.Length);
                loginHandler.OnReceive(login, otherFrame, 0, otherFrame.Length);
            }

            await Assert.That(Count(metrics, "aaemu_game_packets_rejected_total", "network=\"game\",reason=\"unknown_opcode\""))
                .IsEqualTo(5L);
            await Assert.That(Count(metrics, "aaemu_game_packets_rejected_total", "network=\"stream\",reason=\"unknown_opcode\""))
                .IsEqualTo(5L);
            await Assert.That(Count(metrics, "aaemu_game_packets_rejected_total", "network=\"login\",reason=\"unknown_opcode\""))
                .IsEqualTo(5L);
            await Assert.That(target.Logs.Count).IsEqualTo(3 * ConnectionEventLimiter.DefaultLimit);
            await Assert.That(string.Join('\n', target.Logs)).DoesNotContain("private-payload");
        }
        finally { LogManager.Configuration = original; }
    }

    [Test]
    public async Task StateRateAndMalformedRejections_RecordMatchingDisconnectReasons()
    {
        var metrics = new GameSecurityMetrics();
        var handler = new GameProtocolHandler();
        var state = new GameConnection(new Session(1), securityMetrics: metrics);
        var frame = GameFrame(AAEmu.Game.Core.Packets.C2G.CSOffsets.CSRequestUIDataPacket, []);
        handler.OnReceive(state, frame, 0, frame.Length);
        var rate = new GameConnection(new Session(2),
            new AAEmu.Game.Models.GameNetworkLimitsConfig { PacketsPerSecond = 1 }, securityMetrics: metrics);
        rate.TryAuthenticate(10);
        handler.OnReceive(rate, frame, 0, frame.Length);
        handler.OnReceive(rate, frame, 0, frame.Length);
        var malformed = new GameConnection(new Session(3), securityMetrics: metrics);
        handler.OnReceive(malformed, [0, 0], 0, 2);

        await Assert.That(Count(metrics, "aaemu_game_packets_rejected_total", "network=\"game\",reason=\"invalid_state\""))
            .IsEqualTo(1L);
        await Assert.That(Count(metrics, "aaemu_game_packets_rejected_total", "network=\"game\",reason=\"rate_limit\""))
            .IsEqualTo(1L);
        await Assert.That(Count(metrics, "aaemu_game_packets_rejected_total", "network=\"game\",reason=\"malformed\""))
            .IsEqualTo(1L);
        foreach (var reason in new[] { "invalid_state", "rate_limit", "protocol_error" })
            await Assert.That(Count(metrics, "aaemu_game_disconnects_total", $"network=\"game\",reason=\"{reason}\""))
                .IsEqualTo(1L);
    }

    [Test]
    public async Task Disconnects_KeepOnlyFirstReasonAcrossSocketAndCleanupCallbacks()
    {
        var metrics = new GameSecurityMetrics();
        var game = new GameConnection(new Session(1), securityMetrics: metrics);
        game.Shutdown(SecurityDisconnectReason.RateLimit);
        game.MarkClosed();
        game.CompleteDisconnect(() => { }, () => { }, () => { });
        game.Shutdown();
        var stream = new StreamConnection(new Session(2), metrics);
        stream.Shutdown(SecurityDisconnectReason.ProtocolError);
        stream.RecordDisconnect(SecurityDisconnectReason.RemoteClose);
        var login = new LoginConnection(new Session(3), securityMetrics: metrics);
        login.Close(SecurityDisconnectReason.AuthenticationRejected);
        login.RecordDisconnect(SecurityDisconnectReason.RemoteClose);

        await Assert.That(Count(metrics, "aaemu_game_disconnects_total", "network=\"game\",reason=\"rate_limit\""))
            .IsEqualTo(1L);
        await Assert.That(Count(metrics, "aaemu_game_disconnects_total", "network=\"stream\",reason=\"protocol_error\""))
            .IsEqualTo(1L);
        await Assert.That(Count(metrics, "aaemu_game_disconnects_total", "network=\"login\",reason=\"authentication_rejected\""))
            .IsEqualTo(1L);
        foreach (var network in new[] { "game", "stream", "login" })
            await Assert.That(Count(metrics, "aaemu_game_disconnects_total", $"network=\"{network}\",reason=\"remote_close\""))
                .IsEqualTo(0L);
    }

    [Test]
    public async Task TokenRejections_CountWorldStreamAndReconnectWithoutExposingCredentials()
    {
        var metrics = new GameSecurityMetrics();
        var original = LogManager.Configuration;
        var originalNetwork = AppConfiguration.Instance.Network;
        using var target = CaptureEvents();
        try
        {
            AppConfiguration.Instance.Network = new AppConfiguration.NetworkConfig();
            const uint secret = 0xfedcba98;
            var enter = new EnterWorldManager(Mock.Of<IAccountManager>().Object, Mock.Of<IStreamManager>().Object,
                Mock.Of<IQuestManager>().Object, Mock.Of<IChatManager>().Object, Mock.Of<IFamilyManager>().Object,
                Mock.Of<IWorldManager>().Object);
            enter.Login(new GameConnection(new Session(1), securityMetrics: metrics), 10, secret);
            var table = new GameConnectionTable();
            var streamManager = new StreamManager(table, () => 1);
            var stream = new StreamConnection(new Session(2), metrics);
            var reconnect = new ReconnectTokenManager(table, new FakeTimeProvider(), () => 1);
            var login = new LoginConnection(new Session(3), securityMetrics: metrics);
            for (var index = 0; index < 5; ++index)
            {
                streamManager.TryJoin(stream, 10, secret);
                reconnect.Acknowledge(secret, login);
            }

            await Assert.That(Count(metrics, "aaemu_game_token_failures_total", "kind=\"world_cookie\",reason=\"not_found\""))
                .IsEqualTo(1L);
            await Assert.That(Count(metrics, "aaemu_game_token_failures_total", "kind=\"stream\",reason=\"not_found\""))
                .IsEqualTo(5L);
            await Assert.That(Count(metrics, "aaemu_game_token_failures_total", "kind=\"reconnect\",reason=\"not_found\""))
                .IsEqualTo(5L);
            await Assert.That(target.Logs.Count).IsEqualTo(1 + 2 * ConnectionEventLimiter.DefaultLimit);
            await Assert.That(string.Join('\n', target.Logs)).DoesNotContain(secret.ToString());
            await Assert.That(string.Join('\n', target.Logs)).DoesNotContain(secret.ToString("x"));
        }
        finally
        {
            AppConfiguration.Instance.Network = originalNetwork;
            LogManager.Configuration = original;
        }
    }

    [Test]
    public async Task SuspiciousActivity_CountsAcceptedAndDroppedReportsWithoutDescriptions()
    {
        var metrics = new GameSecurityMetrics();
        var original = LogManager.Configuration;
        using var target = CaptureEvents();
        try
        {
            using var manager = new SusManager(Mock.Of<IWorldManager>().Object, metrics) { WriteBatch = _ => { } };
            manager.LogActivity("Cheat", 1, 2, 3, new Vector3(float.NaN, 0, 0), "private-description");
            for (var index = 0; index < SusManager.QueueCapacity; ++index)
                manager.LogActivity("Cheat", "private-description");
            manager.LogActivity("Cheat", "private-description");
            manager.Dispose();
            manager.LogActivity("unknown-category-private", "private-description");

            await Assert.That(Count(metrics, "aaemu_game_suspicious_events_total", "category=\"cheat\",outcome=\"queued\""))
                .IsEqualTo((long)SusManager.QueueCapacity);
            await Assert.That(Count(metrics, "aaemu_game_suspicious_events_total", "category=\"cheat\",outcome=\"invalid\""))
                .IsEqualTo(1L);
            await Assert.That(Count(metrics, "aaemu_game_suspicious_events_total", "category=\"cheat\",outcome=\"queue_full\""))
                .IsEqualTo(1L);
            await Assert.That(Count(metrics, "aaemu_game_suspicious_events_total", "category=\"other\",outcome=\"stopped\""))
                .IsEqualTo(1L);
            await Assert.That(string.Join('\n', target.Logs)).DoesNotContain("private-description");
            await Assert.That(string.Join('\n', target.Logs)).DoesNotContain("unknown-category-private");
        }
        finally { LogManager.Configuration = original; }
    }

    [Test]
    public async Task CommandCompletion_CountsDeferredOutcomeOnceAndKeepsPrivateDataOnlyInAuditStore()
    {
        var metrics = new GameSecurityMetrics();
        var original = LogManager.Configuration;
        using var target = CaptureEvents(LogLevel.Info);
        try
        {
            var store = new AuditStore();
            var entry = new CommandAuditEntry { Arguments = ["private-argument"], Command = "private-command" };
            using (new CommandAuditContext(entry, store, metrics))
            {
                var complete = CommandAuditContext.DeferResult();
                complete("completed", "private-detail");
                complete("failed", "duplicate");
            }

            await Assert.That(Count(metrics, "aaemu_game_gm_commands_total", "source=\"game_chat\",outcome=\"completed\""))
                .IsEqualTo(1L);
            await Assert.That(Count(metrics, "aaemu_game_gm_commands_total", "source=\"game_chat\",outcome=\"failed\""))
                .IsEqualTo(0L);
            await Assert.That(store.Completions).IsEqualTo(1);
            await Assert.That(store.Entry.Arguments.Single()).IsEqualTo("private-argument");
            await Assert.That(store.Entry.Detail).IsEqualTo("private-detail");
            await Assert.That(target.Logs.Count).IsEqualTo(1);
            await Assert.That(target.Logs.Single()).DoesNotContain("private-");
        }
        finally { LogManager.Configuration = original; }
    }

    [Test]
    public async Task CommandAuditFailure_RecordsFailedPersistenceInsteadOfSuccessfulExecution()
    {
        var metrics = new GameSecurityMetrics();
        using (var context = new CommandAuditContext(new CommandAuditEntry { Source = "web-api" },
            new AuditStore { Fail = true }, metrics))
            context.Complete();

        await Assert.That(Count(metrics, "aaemu_game_gm_commands_total", "source=\"web_api\",outcome=\"audit_finish_failed\""))
            .IsEqualTo(1L);
        await Assert.That(Count(metrics, "aaemu_game_gm_commands_total", "source=\"web_api\",outcome=\"completed\""))
            .IsEqualTo(0L);
    }

    private static long Count(GameSecurityMetrics metrics, string name, string labels)
    {
        var prefix = name + "{" + labels + "} ";
        return long.Parse(metrics.Collect().Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..]);
    }

    private static MemoryTarget CaptureEvents(LogLevel level = null)
    {
        var target = new MemoryTarget { Layout = "${message}" };
        var config = new LoggingConfiguration();
        config.AddRule(level ?? LogLevel.Warn, LogLevel.Fatal, target);
        LogManager.Configuration = config;
        return target;
    }

    private static byte[] GameFrame(ushort opcode, byte[] payload) => new PacketStream().Write(new PacketStream()
        .Write((byte)0).Write((byte)1).Write((byte)0).Write((byte)0).Write(opcode).Write(payload)).GetBytes();
    private static byte[] StreamFrame(ushort opcode, byte[] payload) => new PacketStream().Write(new PacketStream()
        .Write(opcode).Write(payload)).GetBytes();

    private sealed class Session(uint id) : ISession
    {
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => id;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private sealed class AuditStore : ICommandAuditStore
    {
        public bool Fail { get; init; }
        public int Completions { get; private set; }
        public CommandAuditEntry Entry { get; private set; }
        public void Start(CommandAuditEntry entry) { }
        public void Complete(CommandAuditEntry entry)
        {
            if (Fail) throw new IOException("Injected failure.");
            ++Completions;
            Entry = entry;
        }
    }
}
