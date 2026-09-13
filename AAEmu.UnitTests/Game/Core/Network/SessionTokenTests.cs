using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Network;

public class SessionTokenTests
{
    [Test]
    public async Task StreamToken_BindsExactAccountAndConnectionWithoutUsingItsId()
    {
        var table = new GameConnectionTable();
        var game = Game(table, 7, 20);
        var manager = new StreamManager(table, () => 0xfedcba98u);
        var token = manager.AddToken(game);
        var stream = new StreamConnection(new RecordingSession(9));

        await Assert.That(token).IsNotEqualTo(game.Id);
        await Assert.That(manager.TryJoin(stream, 30, token)).IsFalse();
        await Assert.That(manager.TryJoin(stream, 20, game.Id)).IsFalse();
        await Assert.That(manager.TryJoin(stream, 20, 0)).IsFalse();
        await Assert.That(stream.GameConnection).IsNull();
        await Assert.That(manager.TryJoin(stream, 20, token)).IsTrue();
        await Assert.That(stream.GameConnection).IsSameReferenceAs(game);

        // A failed guess does not consume another account's valid token.
        var reopenedStream = new StreamConnection(new RecordingSession(10));
        await Assert.That(manager.TryJoin(reopenedStream, 20, token)).IsTrue();
        await Assert.That(reopenedStream.GameConnection).IsSameReferenceAs(game);
    }

    [Test]
    public async Task StreamToken_AllocationSkipsZeroAndCollisions()
    {
        var table = new GameConnectionTable();
        var first = Game(table, 7, 20);
        var second = Game(table, 8, 30);
        var values = new Queue<uint>([0, 100, 100, 200]);
        var manager = new StreamManager(table, values.Dequeue);

        await Assert.That(manager.AddToken(first)).IsEqualTo(100u);
        await Assert.That(manager.AddToken(first)).IsEqualTo(100u);
        await Assert.That(manager.AddToken(second)).IsEqualTo(200u);
        await Assert.That(values).IsEmpty();
    }

    [Test]
    [Arguments("closed")]
    [Arguments("removed")]
    [Arguments("replaced")]
    public async Task StreamToken_RejectsStaleGameConnection(string change)
    {
        var table = new GameConnectionTable();
        var session = new RecordingSession(7);
        var game = Game(table, session, 20);
        var manager = new StreamManager(table, () => 100);
        var token = manager.AddToken(game);
        if (change == "closed")
            game.Shutdown();
        else
        {
            table.RemoveConnection(session);
            if (change == "replaced")
                Game(table, 7, 20);
        }
        var stream = new StreamConnection(new RecordingSession(9));

        await Assert.That(manager.TryJoin(stream, 20, token)).IsFalse();
        await Assert.That(stream.GameConnection).IsNull();
    }

    [Test]
    public async Task StreamToken_RemovalUsesConnectionIdentityAndPreventsRebinding()
    {
        var table = new GameConnectionTable();
        var first = Game(table, 7, 20);
        var second = Game(table, 8, 30);
        uint next = 100;
        var manager = new StreamManager(table, () => next++);
        var firstToken = manager.AddToken(first);
        var secondToken = manager.AddToken(second);
        var stream = new StreamConnection(new RecordingSession(9));
        manager.TryJoin(stream, 20, firstToken);

        await Assert.That(manager.TryJoin(stream, 30, secondToken)).IsFalse();
        await Assert.That(stream.GameConnection).IsSameReferenceAs(first);
        manager.RemoveToken(new GameConnection(new RecordingSession(first.Id)));
        await Assert.That(manager.TryJoin(stream, 20, firstToken)).IsTrue();
        manager.RemoveToken(first);
        await Assert.That(manager.TryJoin(stream, 20, firstToken)).IsFalse();
        await Assert.That(manager.TryJoin(new StreamConnection(new RecordingSession(10)), 30, secondToken)).IsTrue();
    }

    [Test]
    public async Task StreamToken_ConcurrentIssuanceKeepsOneTokenForTheConnection()
    {
        var table = new GameConnectionTable();
        var game = Game(table, 7, 20);
        var next = 100;
        var manager = new StreamManager(table, () => (uint)Interlocked.Increment(ref next));
        var tokens = new uint[32];
        Parallel.For(0, tokens.Length, index => tokens[index] = manager.AddToken(game));

        await Assert.That(tokens.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ReconnectAcknowledgement_UsesSecretTokenAndRunsOnlyOnce()
    {
        var table = new GameConnectionTable();
        var session = new RecordingSession(7);
        var game = Game(table, session, 20);
        var manager = new ReconnectTokenManager(table, new FakeTimeProvider(), () => 0xfedcba98u);
        var token = manager.Issue(game);

        await Assert.That(manager.Acknowledge(game.Id)).IsFalse();
        await Assert.That(manager.Acknowledge(token)).IsTrue();
        await Assert.That(manager.Acknowledge(token)).IsFalse();
        await Assert.That(session.Sent.Count).IsEqualTo(1);
        var expected = new SCReconnectAuthPacket(token).Encode().GetBytes();
        await Assert.That(session.Sent[0]).IsEquivalentTo(expected);
    }

    [Test]
    public async Task ReconnectAcknowledgement_ExpiresAtTheDeadline()
    {
        var table = new GameConnectionTable();
        var session = new RecordingSession(7);
        var game = Game(table, session, 20);
        var time = new FakeTimeProvider();
        var manager = new ReconnectTokenManager(table, time, () => 100);
        var token = manager.Issue(game);
        time.Advance(ReconnectTokenManager.Lifetime);

        await Assert.That(manager.Acknowledge(token)).IsFalse();
        await Assert.That(session.Sent).IsEmpty();
    }

    [Test]
    public async Task ReconnectAcknowledgement_RejectsOldTokenAfterReplacementOrRemoval()
    {
        var table = new GameConnectionTable();
        var session = new RecordingSession(7);
        var game = Game(table, session, 20);
        uint next = 100;
        var manager = new ReconnectTokenManager(table, new FakeTimeProvider(), () => next++);
        var old = manager.Issue(game);
        var current = manager.Issue(game);

        await Assert.That(manager.Acknowledge(old)).IsFalse();
        manager.Remove(new GameConnection(new RecordingSession(game.Id)));
        await Assert.That(manager.Acknowledge(current)).IsTrue();
        current = manager.Issue(game);
        manager.Remove(game);
        await Assert.That(manager.Acknowledge(current)).IsFalse();
        await Assert.That(session.Sent.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("closed")]
    [Arguments("removed")]
    [Arguments("replaced")]
    [Arguments("world")]
    public async Task ReconnectAcknowledgement_RejectsStaleOrNonLobbyConnection(string change)
    {
        var table = new GameConnectionTable();
        var session = new RecordingSession(7);
        var game = Game(table, session, 20);
        var manager = new ReconnectTokenManager(table, new FakeTimeProvider(), () => 100);
        var token = manager.Issue(game);
        if (change == "closed")
            game.Shutdown();
        else if (change == "world")
            game.State = GameState.World;
        else
        {
            table.RemoveConnection(session);
            if (change == "replaced")
                Game(table, 7, 20);
        }

        await Assert.That(manager.Acknowledge(token)).IsFalse();
        await Assert.That(session.Sent).IsEmpty();
    }

    [Test]
    public async Task ReconnectAcknowledgement_ConcurrentRepliesSendOnePacket()
    {
        var table = new GameConnectionTable();
        var session = new RecordingSession(7);
        var game = Game(table, session, 20);
        var manager = new ReconnectTokenManager(table, new FakeTimeProvider(), () => 100);
        var token = manager.Issue(game);
        var accepted = new bool[32];
        Parallel.For(0, accepted.Length, index => accepted[index] = manager.Acknowledge(token));

        await Assert.That(accepted.Count(value => value)).IsEqualTo(1);
        await Assert.That(session.Sent.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TokenPackets_PreserveAll32Bits()
    {
        const uint token = 0xfedcba98;
        var reconnect = new SCReconnectAuthPacket(token).Write(new PacketStream());
        await Assert.That(reconnect.ReadUInt32()).IsEqualTo(token);
        await Assert.That(reconnect.LeftBytes).IsEqualTo(0);
        var enter = new X2EnterWorldResponsePacket(0, false, token, 1250).Write(new PacketStream());
        await Assert.That(enter.ReadUInt16()).IsEqualTo((ushort)0);
        await Assert.That(enter.ReadBoolean()).IsFalse();
        await Assert.That(enter.ReadUInt32()).IsEqualTo(token);
        await Assert.That(enter.ReadUInt16()).IsEqualTo((ushort)1250);
        enter.ReadUInt64();
        await Assert.That(enter.ReadUInt16()).IsEqualTo((ushort)0);
        await Assert.That(enter.ReadString()).IsEqualTo("");
        await Assert.That(enter.LeftBytes).IsEqualTo(0);
    }

    private static GameConnection Game(GameConnectionTable table, uint id, uint accountId) => Game(table, new RecordingSession(id), accountId);

    private static GameConnection Game(GameConnectionTable table, RecordingSession session, uint accountId)
    {
        var game = new GameConnection(session);
        game.TryAuthenticate(accountId);
        game.State = GameState.Lobby;
        table.AddConnection(game);
        return game;
    }

    private sealed class RecordingSession(uint id) : ISession
    {
        public List<byte[]> Sent { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => id;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Sent.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
