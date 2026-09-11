using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;

namespace AAEmu.UnitTests.Game.Core.Packets;

public class GameAuthenticationTests
{
    [Test]
    [Arguments(CSOffsets.CSCreateCharacterPacket)]
    [Arguments(CSOffsets.CSSelectCharacterPacket)]
    [Arguments(CSOffsets.CSSpawnCharacterPacket)]
    public async Task PreauthLobbyPacket_ClosesSocketBeforeHandler(ushort opcode)
    {
        var session = new RecordingSession();
        var connection = new GameConnection(session);
        var protocol = new GameProtocolHandler();
        protocol.RegisterPacket(opcode, 1, typeof(ProbePacket));
        var data = Frame(opcode);
        protocol.OnReceive(connection, data, 0, data.Length);

        await Assert.That(session.Closes).IsEqualTo(1);
        await Assert.That(connection.GetAttribute("decoded")).IsNull();
        await Assert.That(connection.AccountId).IsEqualTo(0u);
    }

    [Test]
    public async Task DirectLobbyHandlers_RejectAccountZeroBeforeReadingPayload()
    {
        var packets = new GamePacket[] { new CSCreateCharacterPacket(), new CSSelectCharacterPacket(), new CSSpawnCharacterPacket() };
        foreach (var packet in packets)
        {
            var session = new RecordingSession();
            packet.Connection = new GameConnection(session);
            packet.Read(new PacketStream());
            await Assert.That(session.Closes).IsEqualTo(1);
        }
    }

    [Test]
    public async Task PositiveIdWithoutAuthentication_DoesNotPermitLobbyPackets()
    {
        var connection = new GameConnection(new RecordingSession()) { AccountId = 20 };
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSCreateCharacterPacket, 1)).IsFalse();
    }

    [Test]
    public async Task Preauth_OnlyLevelOneEnterWorldIsPermitted()
    {
        var connection = new GameConnection(new RecordingSession());
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.X2EnterWorldPacket, 1)).IsTrue();
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.X2EnterWorldPacket, 2)).IsFalse();
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSSendChatMessagePacket, 1)).IsFalse();
    }

    [Test]
    public async Task AuthenticatedLobbyPacket_ReachesHandlerButDuplicateEnterWorldDoesNot()
    {
        var session = new RecordingSession();
        var connection = new GameConnection(session);
        connection.TryAuthenticate(20);
        var protocol = new GameProtocolHandler();
        protocol.RegisterPacket(CSOffsets.CSCreateCharacterPacket, 1, typeof(ProbePacket));
        var data = Frame(CSOffsets.CSCreateCharacterPacket);
        protocol.OnReceive(connection, data, 0, data.Length);

        await Assert.That(connection.GetAttribute("decoded")).IsEqualTo(true);
        await Assert.That(session.Closes).IsEqualTo(0);
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.X2EnterWorldPacket, 1)).IsFalse();
    }

    [Test]
    public async Task Authentication_RequiresPositiveAccountAndRunsOnce()
    {
        var connection = new GameConnection(new RecordingSession());
        await Assert.That(connection.TryAuthenticate(0)).IsFalse();
        await Assert.That(connection.TryAuthenticate(20)).IsTrue();
        await Assert.That(connection.TryAuthenticate(30)).IsFalse();
        await Assert.That(connection.AccountId).IsEqualTo(20u);
    }

    [Test]
    public async Task AuthenticationTimeout_ClosesIdleSocket()
    {
        var session = new RecordingSession();
        var connection = new GameConnection(session);
        connection.StartAuthenticationTimeout(TimeSpan.FromMilliseconds(10));
        await session.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(connection.IsClosed).IsTrue();
        await Assert.That(session.Closes).IsEqualTo(1);
        await Assert.That(connection.TryAuthenticate(20)).IsFalse();
    }

    [Test]
    public async Task AuthenticationTimeout_DoesNotCloseAuthenticatedSocket()
    {
        var session = new RecordingSession();
        var connection = new GameConnection(session);
        connection.TryAuthenticate(20);
        connection.CloseIfUnauthenticated();
        await Assert.That(session.Closes).IsEqualTo(0);
    }

    [Test]
    public async Task AccountZeroDisconnectAndLoad_DoNotCallManagersOrDatabase()
    {
        // No singleton container or database exists in this test.
        var session = new RecordingSession();
        var connection = new GameConnection(session);
        connection.OnDisconnect();
        connection.LoadAccount();
        await Assert.That(session.Closes).IsEqualTo(1);
        await Assert.That(connection.AccountId).IsEqualTo(0u);
    }

    [Test]
    public async Task Kick_SaveRunsBeforeSocketClose()
    {
        var calls = new List<string>();
        GameConnection.DisconnectWithSave(() => calls.Add("notify"), () => calls.Add("save"), () => calls.Add("close"));
        await Assert.That(string.Join(',', calls)).IsEqualTo("notify,save,close");
    }

    [Test]
    public async Task Kick_NotificationFails_StillSavesAndCloses()
    {
        var calls = new List<string>();
        await Assert.That(() => GameConnection.DisconnectWithSave(
            () => throw new IOException("send failed"), () => calls.Add("save"), () => calls.Add("close"))).Throws<IOException>();
        await Assert.That(string.Join(',', calls)).IsEqualTo("save,close");
    }

    [Test]
    public async Task Kick_SaveFails_StillClosesSocket()
    {
        var closed = false;
        await Assert.That(() => GameConnection.DisconnectWithSave(
            () => { }, () => throw new IOException("save failed"), () => closed = true)).Throws<IOException>();
        await Assert.That(closed).IsTrue();
    }

    [Test]
    public async Task Kick_PreliminaryCleanupFails_StillTriesSaveAndClosesSocket()
    {
        var calls = new List<string>();
        await Assert.That(() => GameConnection.DisconnectWithSave(() => { },
            () => GameConnection.CleanupThenSave(() => throw new IOException("cleanup failed"), () => calls.Add("save")),
            () => calls.Add("close"))).Throws<IOException>();
        await Assert.That(string.Join(',', calls)).IsEqualTo("save,close");
    }

    private static byte[] Frame(ushort opcode)
        => new PacketStream().Write(new PacketStream().Write((byte)0).Write((byte)1)
            .Write((byte)0).Write((byte)0).Write(opcode)).GetBytes();

    public sealed class ProbePacket() : GamePacket(CSOffsets.CSCreateCharacterPacket, 1)
    {
        public override void Read(PacketStream stream) => Connection.AddAttribute("decoded", true);
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        private int _closes;
        public int Closes => Volatile.Read(ref _closes);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) => _attributes[name] = attribute;
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { Interlocked.Increment(ref _closes); Closed.TrySetResult(); }
    }
}
