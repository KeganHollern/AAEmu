using System.Net;
using System.Net.Sockets;
using NetCoreServer;
using NLog;

namespace AAEmu.Commons.Network.Core;

public class Server(IPAddress address, int port, IBaseProtocolHandler protocolHandler)
    : TcpServer(address, port)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly object _sessionsLock = new();
    private readonly HashSet<Session> _sessions = [];

    public IBaseProtocolHandler GetHandler() => protocolHandler;

    protected override TcpSession CreateSession() => new Session(this);

    protected override void OnStarted()
    {
        Logger.Info($"TCP server listening start on {Endpoint}");
    }

    protected override void OnStopped()
    {
        Logger.Info("TCP server listener stopped!");
    }

    protected override void OnConnected(TcpSession session)
    {
        var aaemuSession = (Session)session;
        lock (_sessionsLock)
        {
            // NetCoreServer can deliver OnConnected after an immediate disconnect.
            if (!aaemuSession.IsConnected)
            {
                Logger.Debug($"Ignoring late connect callback for disconnected session id: {session.Id}");
                return;
            }

            // Session caches the endpoint in OnConnecting before receive/disconnect races can occur.
            Logger.Info($"Connect from {aaemuSession.RemoteEndPoint} established, session id: {session.Id}");
            _sessions.Add(aaemuSession);
        }
    }

    protected override void OnDisconnected(TcpSession session)
    {
        lock (_sessionsLock)
        {
            Logger.Info($"Connect from session id: {session.Id} disconnected");
            _sessions.Remove((Session)session);
        }
    }

    protected override void OnError(SocketError error)
    {
        Logger.Error($"TCP server SocketError: {error}");
    }

    public Session GetSession(Func<Session, bool> func)
    {
        return GetSessions().SingleOrDefault(func);
    }

    public HashSet<Session> GetSessions()
    {
        lock (_sessionsLock)
        {
            return [.. _sessions];
        }
    }

    public IEnumerable<Session> GetSessions(Func<Session, bool> func)
    {
        return GetSessions().Where(func).ToArray();
    }
}
