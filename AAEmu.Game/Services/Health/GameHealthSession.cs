using System.Net;
using System.Net.Sockets;

using NetCoreServer;

using NLog;

namespace AAEmu.Game.Services.Health;

internal class GameHealthSession(HttpServer server, GameHealthState healthState) : HttpSession(server)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    protected override void OnReceivedRequest(HttpRequest request)
    {
        HttpResponse response;
        if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            response = CreateResponse(HttpStatusCode.NotFound, "Not found");
        }
        else if (string.Equals(request.Url, "/health/live", StringComparison.Ordinal))
        {
            response = CreateResponse(HttpStatusCode.OK, "Live");
        }
        else if (string.Equals(request.Url, "/health/ready", StringComparison.Ordinal))
        {
            response = healthState.IsReady
                ? CreateResponse(HttpStatusCode.OK, "Ready")
                : CreateResponse(HttpStatusCode.ServiceUnavailable, "Not ready");
        }
        else
        {
            response = CreateResponse(HttpStatusCode.NotFound, "Not found");
        }

        SendHealthResponseAsync(response);
    }

    protected virtual void SendHealthResponseAsync(HttpResponse response)
    {
        SendResponseAsync(response);
    }

    protected override void OnReceivedRequestError(HttpRequest request, string error)
    {
        Logger.Warn($"Game health request error: {error}");
    }

    protected override void OnError(SocketError error)
    {
        Logger.Warn($"Game health session caught an error: {error}");
    }

    private static HttpResponse CreateResponse(HttpStatusCode status, string body)
    {
        var response = new HttpResponse((int)status);
        response.SetContentType("text/plain");
        response.SetBody(body);
        return response;
    }
}
