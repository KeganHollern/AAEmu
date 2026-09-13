using System.Net;
using System.Net.Sockets;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.UnitTests.Game.Core.Packets;

[NotInParallel]
public sealed class GameAuthenticationRaceTests
{
    [Test]
    public async Task AuthenticationTimeout_SynchronousClose_DoesNotHoldAuthenticationLock()
    {
        using var receiveOwnsSession = new ManualResetEventSlim();
        using var closeEntered = new ManualResetEventSlim();
        var session = new SynchronousCloseSession();
        var connection = new GameConnection(session);
        var callbackTimedOut = false;
        session.OnClose = () =>
        {
            closeEntered.Set();
            // NetCoreServer invokes OnDisconnected synchronously inside Disconnect.
            // Bound this wait so a lock-order regression fails without a deadlock.
            if (!Monitor.TryEnter(connection.SessionSyncRoot, TimeSpan.FromSeconds(2)))
            {
                callbackTimedOut = true;
                return;
            }
            try { connection.CompleteDisconnect(() => { }, () => { }, () => { }); }
            finally { Monitor.Exit(connection.SessionSyncRoot); }
        };

        var authenticate = Task.Run(() =>
        {
            lock (connection.SessionSyncRoot)
            {
                receiveOwnsSession.Set();
                if (!closeEntered.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The synchronous close callback did not start.");
                return connection.TryAuthenticate(42);
            }
        });
        if (!receiveOwnsSession.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The receive task did not acquire the session lock.");
        var timeout = Task.Run(connection.CloseIfUnauthenticated);
        await Task.WhenAll(authenticate, timeout).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(await authenticate).IsFalse();
        await Assert.That(callbackTimedOut).IsFalse();
        await Assert.That(connection.IsClosed).IsTrue();
        await Assert.That(session.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task NetworkDisconnect_FromSendCallback_DoesNotWaitForReceiveLock()
    {
        using var receiveOwnsSession = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        var sendLock = new object();
        var session = new SynchronousCloseSession();
        var connection = new GameConnection(session);
        var table = GameConnectionTable.Instance;
        await Assert.That(table.AddConnection(connection)).IsTrue();
        try
        {
            var receive = Task.Run(() =>
            {
                lock (connection.SessionSyncRoot)
                {
                    receiveOwnsSession.Set();
                    if (!callbackEntered.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The send callback did not start.");
                    if (!Monitor.TryEnter(sendLock, TimeSpan.FromSeconds(2)))
                        return false;
                    Monitor.Exit(sendLock);
                    return true;
                }
            });
            if (!receiveOwnsSession.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The receive task did not acquire the session lock.");
            var callback = Task.Run(() =>
            {
                // NetCoreServer can hold its send lock when a failed synchronous
                // SendAsync completion invokes the disconnect callback.
                lock (sendLock)
                {
                    callbackEntered.Set();
                    new GameProtocolHandler().OnDisconnect(session);
                }
            });
            await Task.WhenAll(receive, callback).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(await receive).IsTrue();
            await Assert.That(connection.IsClosed).IsTrue();
            await Assert.That(SpinWait.SpinUntil(() => table.GetConnection(session) == null,
                TimeSpan.FromSeconds(5))).IsTrue();
        }
        finally { table.RemoveConnection(session); }
    }

    [Test]
    public async Task NetworkDisconnect_InsideTheReceiveHandler_DefersCleanupUntilTheHandlerReturns()
    {
        var session = new SynchronousCloseSession();
        var connection = new GameConnection(session);
        var table = GameConnectionTable.Instance;
        await Assert.That(table.AddConnection(connection)).IsTrue();
        try
        {
            bool stillRegistered;
            lock (connection.SessionSyncRoot)
            {
                new GameProtocolHandler().OnDisconnect(session);
                stillRegistered = ReferenceEquals(table.GetConnection(session), connection);
            }
            await Assert.That(stillRegistered).IsTrue();
            await Assert.That(connection.IsClosed).IsTrue();
            await Assert.That(SpinWait.SpinUntil(() => table.GetConnection(session) == null,
                TimeSpan.FromSeconds(5))).IsTrue();
        }
        finally { table.RemoveConnection(session); }
    }

    private sealed class SynchronousCloseSession : ISession
    {
        internal Action OnClose { get; set; }
        internal int Closes { get; private set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close()
        {
            Closes++;
            OnClose?.Invoke();
        }
    }
}
