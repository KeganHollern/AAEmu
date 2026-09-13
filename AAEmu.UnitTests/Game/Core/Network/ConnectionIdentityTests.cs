using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;

namespace AAEmu.UnitTests.Game.Core.Network;

public class ConnectionIdentityTests
{
    [Test]
    public async Task GameCollision_ClosesOnlyRejectedSocketAndCannotRouteOrRemoveAcceptedConnection()
    {
        var table = new GameConnectionTable();
        var acceptedSession = new RecordingSession(7);
        var rejectedSession = new RecordingSession(7);
        var accepted = new GameConnection(acceptedSession);
        var rejected = new GameConnection(rejectedSession);
        rejectedSession.OnClose = () => table.RemoveConnection(rejectedSession);

        await Assert.That(table.AddConnection(accepted)).IsTrue();
        await Assert.That(table.AddConnection(rejected)).IsFalse();

        await Assert.That(rejectedSession.Closes).IsEqualTo(1);
        await Assert.That(acceptedSession.Closes).IsEqualTo(0);
        await Assert.That(table.GetConnection(rejectedSession)).IsNull();
        await Assert.That(table.RemoveConnection(rejectedSession)).IsNull();
        await Assert.That(table.GetConnection(acceptedSession)).IsSameReferenceAs(accepted);
        await Assert.That(table.GetConnections().Count).IsEqualTo(1);
    }

    [Test]
    public async Task StreamCollision_ClosesOnlyRejectedSocketAndCannotRouteOrRemoveAcceptedConnection()
    {
        var table = new StreamConnectionTable();
        var acceptedSession = new RecordingSession(7);
        var rejectedSession = new RecordingSession(7);
        var accepted = new StreamConnection(acceptedSession);
        var rejected = new StreamConnection(rejectedSession);
        rejectedSession.OnClose = () => table.RemoveConnection(rejectedSession);

        await Assert.That(table.AddConnection(accepted)).IsTrue();
        await Assert.That(table.AddConnection(rejected)).IsFalse();

        await Assert.That(rejectedSession.Closes).IsEqualTo(1);
        await Assert.That(acceptedSession.Closes).IsEqualTo(0);
        await Assert.That(table.GetConnection(rejectedSession)).IsNull();
        await Assert.That(table.RemoveConnection(rejectedSession)).IsNull();
        await Assert.That(table.GetConnection(acceptedSession)).IsSameReferenceAs(accepted);
        await Assert.That(table.GetConnections().Count).IsEqualTo(1);
    }

    [Test]
    public async Task LateGameDisconnect_DoesNotRemoveAReplacementWithTheSameId()
    {
        var table = new GameConnectionTable();
        var oldSession = new RecordingSession(7);
        var nextSession = new RecordingSession(7);
        var old = new GameConnection(oldSession);
        var next = new GameConnection(nextSession);
        table.AddConnection(old);
        table.RemoveConnection(oldSession);
        table.AddConnection(next);

        await Assert.That(table.RemoveConnection(oldSession)).IsNull();
        await Assert.That(table.GetConnection(oldSession)).IsNull();
        await Assert.That(table.GetConnection(nextSession)).IsSameReferenceAs(next);
    }

    [Test]
    public async Task LateStreamDisconnect_DoesNotRemoveAReplacementWithTheSameId()
    {
        var table = new StreamConnectionTable();
        var oldSession = new RecordingSession(7);
        var nextSession = new RecordingSession(7);
        table.AddConnection(new StreamConnection(oldSession));
        table.RemoveConnection(oldSession);
        var next = new StreamConnection(nextSession);
        table.AddConnection(next);

        await Assert.That(table.RemoveConnection(oldSession)).IsNull();
        await Assert.That(table.GetConnection(oldSession)).IsNull();
        await Assert.That(table.GetConnection(nextSession)).IsSameReferenceAs(next);
    }

    private sealed class RecordingSession(uint id) : ISession
    {
        public int Closes { get; private set; }
        public Action OnClose { get; set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => id;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { Closes++; OnClose?.Invoke(); }
    }
}
