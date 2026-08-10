using System.Net;
using System.Net.Sockets;

using NetCoreServer;

using NLog;

namespace AAEmu.Game.Services.Health;

internal sealed class GameHealthServer(IPAddress address, int port, GameHealthState healthState)
    : HttpServer(address, port)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    protected override TcpSession CreateSession()
    {
        return new GameHealthSession(this, healthState);
    }

    protected override void OnError(SocketError error)
    {
        Logger.Warn($"Game health server caught an error with code {error}");
    }
}
