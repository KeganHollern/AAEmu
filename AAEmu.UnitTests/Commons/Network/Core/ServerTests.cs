using System.Net;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;

namespace AAEmu.UnitTests.Commons.Network.Core;

public class ServerTests
{
    [Test]
    public async Task OnConnected_DisposedSocket_UsesCachedRemoteEndPoint()
    {
        using var server = new TestServer();
        var session = CreateConnectedSessionWithDisposedSocket(server, 1239);

        server.SimulateConnected(session);

        await Assert.That(session.RemoteEndPoint).IsEqualTo(new IPEndPoint(IPAddress.Loopback, 1239));
        await Assert.That(server.GetSessions()).Contains(session);

        SetProperty(session, nameof(session.IsConnected), false);
        server.SimulateDisconnected(session);
    }

    [Test]
    public async Task OnConnected_AfterDisconnected_DoesNotReinsertSession()
    {
        using var server = new TestServer();
        var session = CreateConnectedSessionWithDisposedSocket(server, 1250);
        server.SimulateConnected(session);

        SetProperty(session, nameof(session.IsConnected), false);
        server.SimulateDisconnected(session);
        server.SimulateConnected(session);

        await Assert.That(server.GetSessions()).IsEmpty();
    }

    [Test]
    public async Task GetSessions_MutatedResult_DoesNotChangeTrackedSessions()
    {
        using var server = new TestServer();
        var session = CreateConnectedSessionWithDisposedSocket(server, 1239);
        server.SimulateConnected(session);

        var snapshot = server.GetSessions();
        snapshot.Clear();
        var current = server.GetSessions();

        await Assert.That(snapshot).IsNotSameReferenceAs(current);
        await Assert.That(snapshot).IsEmpty();
        await Assert.That(current).Contains(session);

        SetProperty(session, nameof(session.IsConnected), false);
        server.SimulateDisconnected(session);
    }

    private static Session CreateConnectedSessionWithDisposedSocket(TestServer server, int port)
    {
        var session = new Session(server);
        SetProperty(session, nameof(session.RemoteEndPoint), new IPEndPoint(IPAddress.Loopback, port));
        SetProperty(session, nameof(session.IsConnected), true);

        session.Socket.Dispose();

        return session;
    }

    private static void SetProperty<T>(Session session, string name, T value)
    {
        typeof(Session).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(session, value);
    }

    private sealed class TestServer : Server
    {
        public TestServer() : base(IPAddress.Loopback, 0, new TestProtocolHandler())
        {
        }

        public void SimulateConnected(Session session)
        {
            base.OnConnected(session);
        }

        public void SimulateDisconnected(Session session)
        {
            base.OnDisconnected(session);
        }
    }

    private sealed class TestProtocolHandler : BaseProtocolHandler
    {
    }
}
