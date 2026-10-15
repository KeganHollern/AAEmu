using System.Net;
using System.Net.Sockets;

using AAEmu.Game.Services.Security;

using NetCoreServer;

using NLog;

namespace AAEmu.Game.Services.Health;

internal sealed class GameHealthServer(IPAddress address, int port, GameHealthState healthState, GameSecurityMetrics metrics = null)
    : HttpServer(address, port)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    protected override TcpSession CreateSession()
    {
        return new GameHealthSession(this, healthState, metrics);
    }

    protected override void OnError(SocketError error)
    {
        Logger.Warn($"Game health server caught an error with code {error}");
    }
}
