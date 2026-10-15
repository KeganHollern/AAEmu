using System.Net;
using System.Net.Sockets;

using AAEmu.Game.Services.Security;

using NetCoreServer;

using NLog;

namespace AAEmu.Game.Services.Health;

internal class GameHealthSession(HttpServer server, GameHealthState healthState, GameSecurityMetrics metrics = null) : HttpSession(server)
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
        else if (string.Equals(request.Url, "/metrics", StringComparison.Ordinal))
        {
            response = CreateResponse(HttpStatusCode.OK, (metrics ?? GameSecurityMetrics.Default).Collect(),
                "text/plain; version=0.0.4; charset=utf-8");
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

    private static HttpResponse CreateResponse(HttpStatusCode status, string body, string contentType = "text/plain")
    {
        var response = new HttpResponse((int)status);
        response.SetHeader("Content-Type", contentType);
        response.SetBody(body);
        return response;
    }
}
