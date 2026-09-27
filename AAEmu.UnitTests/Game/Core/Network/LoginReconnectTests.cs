using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Packets.L2G;
using AAEmu.Game.Models.Tasks.ServerLoad;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Network;

public class LoginReconnectTests
{
    [Test]
    [Arguments(1, 0d, 1d)]
    [Arguments(2, 0.5d, 2.5d)]
    [Arguments(3, 0d, 4d)]
    [Arguments(4, 0d, 8d)]
    [Arguments(5, 0d, 16d)]
    [Arguments(6, 1d, 30d)]
    [Arguments(1000, 1d, 30d)]
    public async Task RetryDelay_GrowsWithJitterWithinBounds(int failures, double jitter, double expected)
    {
        await Assert.That(LoginNetwork.GetRetryDelay(failures, jitter)).IsEqualTo(TimeSpan.FromSeconds(expected));
    }

    [Test]
    [Arguments("refused")]
    [Arguments("rejected")]
    [Arguments("dns")]
    [Arguments("not-started")]
    public async Task Start_RepeatedFailuresBackOffUntilRegistration(string failure)
    {
        await using var fixture = new Fixture(failure);
        fixture.Network.Start();
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            await fixture.Clock.ExpectTimer(10);
            var delay = Math.Min(30, Math.Pow(2, attempt - 1));
            await fixture.Clock.ExpectTimer(delay);
            await Assert.That(fixture.Attempts).IsEqualTo(attempt);
            await Assert.That(fixture.Network.GetConnection()).IsNull();
            fixture.Clock.Advance(delay);
        }
    }

    [Test]
    public async Task RegisteredLink_DisconnectResetsBackoffAndCancelsLoadTask()
    {
        await using var fixture = new Fixture("rejected");
        fixture.Network.Start();
        await fixture.Clock.ExpectTimer(10);
        await fixture.Clock.ExpectTimer(1);
        fixture.Clock.Advance(1);
        await fixture.Clock.ExpectTimer(10);
        await fixture.Clock.ExpectTimer(2);
        fixture.Mode = "accepted";
        fixture.Clock.Advance(2);
        await fixture.Clock.ExpectTimer(10);
        var client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await client.Handler.Registered.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(fixture.Scheduled.Count).IsEqualTo(1);
        client.Disconnect();
        await fixture.Clock.ExpectTimer(1);
        await Assert.That(fixture.Cancelled.Count).IsEqualTo(1);
        await Assert.That(fixture.Cancelled.Single()).IsSameReferenceAs(fixture.Scheduled.Single());
    }

    [Test]
    public async Task RegistrationTimeout_ClosesUnacceptedSocketAndRetries()
    {
        await using var fixture = new Fixture("silent");
        fixture.Network.Start();
        await fixture.Clock.ExpectTimer(10);
        var client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(10);
        await fixture.Clock.ExpectTimer(1);
        await Assert.That(client.IsDisposed).IsTrue();
        await Assert.That(fixture.Network.GetConnection()).IsNull();
        await Assert.That(fixture.Scheduled).IsEmpty();
    }

    [Test]
    public async Task ConnectTimeout_ClosesPendingSocketAndRetries()
    {
        await using var fixture = new Fixture("pending");
        fixture.Network.Start();
        await fixture.Clock.ExpectTimer(10);
        var client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(10);
        await fixture.Clock.ExpectTimer(1);
        await Assert.That(client.IsDisposed).IsTrue();
        await Assert.That(fixture.Network.GetConnection()).IsNull();
    }

    [Test]
    [Arguments("pending")]
    [Arguments("silent")]
    [Arguments("accepted")]
    [Arguments("refused")]
    public async Task Stop_CancelsConnectRegistrationLinkAndBackoff(string mode)
    {
        await using var fixture = new Fixture(mode);
        fixture.Network.Start();
        await fixture.Clock.ExpectTimer(10);
        var client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (mode == "refused")
            await fixture.Clock.ExpectTimer(1);
        fixture.Network.Stop();
        await fixture.Network.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(300);
        await Assert.That(fixture.Attempts).IsEqualTo(1);
        await Assert.That(client.IsDisposed).IsTrue();
        await Assert.That(fixture.Network.GetConnection()).IsNull();
        client.Handler.OnConnect(client);
        await Assert.That(fixture.Network.GetConnection()).IsNull();
    }

    [Test]
    public async Task Stop_CancelsDnsBeforeCreatingSocket()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var clock = new RecordingClock();
        var network = new LoginNetwork(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
            finally
            {
                cancelled = token.IsCancellationRequested;
            }
        }, clock, () => 0, _ => { }, _ => { });
        network.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        network.Stop();
        await network.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cancelled).IsTrue();
        await Assert.That(network.GetConnection()).IsNull();
    }

    [Test]
    public async Task Restart_ObsoleteCallbacksCannotClearOrFeedNewConnection()
    {
        await using var fixture = new Fixture("silent");
        fixture.Network.Start();
        var previous = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await previous.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Network.Stop();
        var stopped = fixture.Network.Completion;
        fixture.Network.Start();
        var current = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await current.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        var connection = fixture.Network.GetConnection();
        previous.Handler.OnDisconnect(previous);
        previous.Handler.OnConnect(previous);
        var rejection = RegisterReply(1);
        previous.Handler.OnReceive(previous, rejection, 0, rejection.Length);
        // A callback with the wrong session must also be ignored by the current handler.
        current.Handler.OnReceive(previous, rejection, 0, rejection.Length);
        current.Handler.OnDisconnect(previous);
        await Assert.That(fixture.Network.GetConnection()).IsSameReferenceAs(connection);
        await Assert.That(current.IsDisposed).IsFalse();
        await Assert.That(current.Handler.Disconnected.IsCompleted).IsFalse();
    }

    [Test]
    public async Task Start_DuplicateCallsUseOneConnectAttempt()
    {
        await using var fixture = new Fixture("pending");
        Parallel.For(0, 32, _ => fixture.Network.Start());
        var client = await fixture.Clients.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(fixture.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Receive_BeforeConnectCopiesAndReassemblesFramesWithoutNullConnection()
    {
        LoginConnection connection = null;
        var handler = new LoginProtocolHandler(value => { connection = value; return true; }, _ => { }, _ => { }, _ => { });
        handler.RegisterPacket(0x1234, typeof(ProbePacket));
        using var client = new ProbeClient(handler, _ => { });
        var frame = new PacketStream().Write(new PacketStream().Write((ushort)0x1234).Write(0x12345678u)).GetBytes();
        handler.OnReceive(client, frame, 0, 1);
        handler.OnReceive(client, frame, 1, frame.Length - 1);
        Array.Fill<byte>(frame, 0); // NetCoreServer reuses its receive buffer.
        handler.OnConnect(client);
        await Assert.That(connection).IsNotNull();
        await Assert.That(connection.LastPacket).IsNull();
        var expected = new ProbePacket { Value = 0x12345678 }.Encode().GetBytes();
        await Assert.That(client.Sent.Any(value => value.SequenceEqual(expected))).IsTrue();
        handler.Stop();
    }

    [Test]
    public async Task Dispose_BetweenConnectFlagsPreventsLateConnectedCallback()
    {
        var published = false;
        var handler = new LoginProtocolHandler(_ => { published = true; return true; }, _ => { }, _ => { }, _ => { });
        using var client = new LoginClient(IPAddress.Loopback, 1, handler);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // NetCoreServer 8.0.7 ProcessConnect clears IsConnecting before setting IsConnected.
        // Reproduce disposal in that gap without depending on the OS callback timing.
        typeof(NetCoreServer.TcpClient).GetField("<Socket>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, socket);
        client.Dispose();
        await Assert.That(socket.SafeHandle.IsClosed).IsTrue();
        typeof(NetCoreServer.TcpClient).GetField("<IsConnected>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, true);
        typeof(LoginClient).GetMethod("OnConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, null);
        await Assert.That(published).IsFalse();
        await Assert.That(client.ConnectAsync()).IsFalse();
    }

    [Test]
    public async Task Loopback_RefusedSocketAndRejectedRegistrationBothRetry()
    {
        // Bind without listening so the first real socket is refused. Then use that same port.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.LocalEndPoint).Port;
        var clock = new RecordingClock();
        var clients = new ConcurrentQueue<LoginClient>();
        var network = new LoginNetwork((handler, _) =>
        {
            var client = new LoginClient(IPAddress.Loopback, port, handler);
            clients.Enqueue(client);
            return Task.FromResult<Client>(client);
        }, clock, () => 0, _ => { }, _ => { });
        try
        {
            network.Start();
            await clock.ExpectTimer(10);
            await clock.ExpectTimer(1);
            await Assert.That(clients.First().IsDisposed).IsTrue();
            await Assert.That(clients.First().Socket.SafeHandle.IsClosed).IsTrue();
            listener.Listen();
            clock.Advance(1);
            using var peer = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await clock.ExpectTimer(10);
            var registration = new byte[512];
            var received = await peer.ReceiveAsync(registration).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(received > 0).IsTrue();
            await peer.SendAsync(RegisterReply(1));
            await clock.ExpectTimer(2);
            await Assert.That(clients.Count).IsEqualTo(2);
            await Assert.That(clients.Last().Socket.SafeHandle.IsClosed).IsTrue();
        }
        finally
        {
            network.Stop();
            await network.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static byte[] RegisterReply(byte result)
    {
        return new PacketStream().Write(new PacketStream().Write(LGOffsets.LGRegisterGameServerPacket).Write(result)).GetBytes();
    }

    public sealed class ProbePacket() : LoginPacket(0x1234)
    {
        public uint Value { get; set; }
        public override void Read(PacketStream stream)
        {
            Value = stream.ReadUInt32();
            Connection.SendPacket(this);
        }
        public override PacketStream Write(PacketStream stream) => stream.Write(Value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public RecordingClock Clock { get; } = new();
        public Channel<ProbeClient> Clients { get; } = Channel.CreateUnbounded<ProbeClient>();
        public ConcurrentQueue<LoadTask> Scheduled { get; } = new();
        public ConcurrentQueue<LoadTask> Cancelled { get; } = new();
        public LoginNetwork Network { get; }
        public string Mode { get; set; }
        public int Attempts;

        public Fixture(string mode)
        {
            Mode = mode;
            Network = new LoginNetwork((handler, _) =>
            {
                Interlocked.Increment(ref Attempts);
                if (Mode == "dns")
                    throw new SocketException((int)SocketError.HostNotFound);
                var client = new ProbeClient(handler, value =>
                {
                    if (Mode == "refused")
                        handler.OnDisconnect(value);
                    else if (Mode != "pending" && Mode != "not-started")
                    {
                        handler.OnConnect(value);
                        if (Mode == "accepted")
                            Network.GetConnection().Register();
                        else if (Mode == "rejected")
                        {
                            var frame = RegisterReply(1);
                            handler.OnReceive(value, frame, 0, frame.Length);
                        }
                    }
                }) { StartResult = Mode != "not-started" };
                Clients.Writer.TryWrite(client);
                return Task.FromResult<Client>(client);
            }, Clock, () => 0, Scheduled.Enqueue, Cancelled.Enqueue);
        }

        public async ValueTask DisposeAsync()
        {
            Network.Stop();
            await Network.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ProbeClient(LoginProtocolHandler handler, Action<ProbeClient> connect)
        : Client(IPAddress.Loopback, 1, handler)
    {
        public LoginProtocolHandler Handler { get; } = handler;
        public bool StartResult { get; init; } = true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Sent { get; } = [];
        public override bool ConnectAsync()
        {
            connect(this);
            Started.TrySetResult();
            return StartResult;
        }
        public override bool Disconnect()
        {
            Handler.OnDisconnect(this);
            return true;
        }
        public override bool SendAsync(byte[] buffer)
        {
            Sent.Add(buffer);
            return true;
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }
        public void Advance(double seconds)
        {
            _clock.Advance(TimeSpan.FromSeconds(seconds));
        }
        public async Task ExpectTimer(double seconds)
        {
            var delay = await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(seconds));
        }
    }
}
