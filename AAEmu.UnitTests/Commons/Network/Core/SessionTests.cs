using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;

namespace AAEmu.UnitTests.Commons.Network.Core;

public class SessionTests
{
    [Test]
    public async Task ConnectedSessions_WithTheSameRemoteEndpointHash_HaveDifferentIdentities()
    {
        using var firstListener = Listener();
        using var secondListener = Listener();
        using var firstClient = Client(new IPEndPoint(IPAddress.Loopback, 0));
        firstClient.Connect(firstListener.LocalEndPoint);
        using var firstSocket = firstListener.Accept();
        using var secondClient = Client((IPEndPoint)firstClient.LocalEndPoint);
        secondClient.Connect(secondListener.LocalEndPoint);
        using var secondSocket = secondListener.Accept();
        using var server = new Server(IPAddress.Loopback, 0, new TestProtocolHandler());
        using var first = new ConnectedSession(server, firstSocket);
        using var second = new ConnectedSession(server, secondSocket);

        first.ConnectForTest();
        second.ConnectForTest();

        await Assert.That(first.RemoteEndPoint).IsEqualTo(second.RemoteEndPoint);
        await Assert.That(first.RemoteEndPoint.GetHashCode()).IsEqualTo(second.RemoteEndPoint.GetHashCode());
        await Assert.That(first.SessionId).IsNotEqualTo(0u);
        await Assert.That(second.SessionId).IsNotEqualTo(0u);
        await Assert.That(first.SessionId).IsNotEqualTo(second.SessionId);
    }

    [Test]
    public async Task ConcurrentSessionConstruction_DoesNotReuseIdentities()
    {
        using var server = new Server(IPAddress.Loopback, 0, new TestProtocolHandler());
        var identities = new uint[1024];
        Parallel.For(0, identities.Length, index =>
        {
            using var session = new Session(server);
            identities[index] = session.SessionId;
        });

        await Assert.That(identities.Distinct().Count()).IsEqualTo(identities.Length);
        await Assert.That(identities.Contains(0u)).IsFalse();
    }

    private static Socket Listener()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);
        return socket;
    }

    private static Socket Client(IPEndPoint localEndpoint)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(localEndpoint);
        return socket;
    }

    private sealed class ConnectedSession : Session
    {
        public ConnectedSession(Server server, Socket socket) : base(server)
        {
            typeof(NetCoreServer.TcpSession).GetField("<Socket>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, socket);
        }

        public void ConnectForTest() => base.OnConnecting();
    }

    private sealed class TestProtocolHandler : BaseProtocolHandler { }
}
