using System.Net;
using System.Net.Sockets;
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

        SetBackingField(session, nameof(session.IsConnected), false);
        server.SimulateDisconnected(session);
    }

    [Test]
    public async Task OnConnected_AfterDisconnected_DoesNotReinsertSession()
    {
        using var server = new TestServer();
        var session = CreateConnectedSessionWithDisposedSocket(server, 1250);
        server.SimulateConnected(session);

        SetBackingField(session, nameof(session.IsConnected), false);
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

        SetBackingField(session, nameof(session.IsConnected), false);
        server.SimulateDisconnected(session);
    }

    private static Session CreateConnectedSessionWithDisposedSocket(TestServer server, int port)
    {
        var session = new Session(server);
        SetBackingField(session, nameof(session.RemoteEndPoint), new IPEndPoint(IPAddress.Loopback, port));
        SetBackingField(session, nameof(session.IsConnected), true);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Dispose();
        SetBackingField(session, nameof(session.Socket), socket);

        return session;
    }

    private static void SetBackingField<T>(Session session, string propertyName, T value)
    {
        var fieldName = $"<{propertyName}>k__BackingField";
        var declaringType = session.GetType();
        while (declaringType != null)
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(session, value);
                return;
            }

            declaringType = declaringType.BaseType;
        }

        throw new MissingFieldException(session.GetType().FullName, fieldName);
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
