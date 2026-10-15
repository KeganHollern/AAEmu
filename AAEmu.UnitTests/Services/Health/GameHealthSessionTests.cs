using System.Net;
using System.Text;

using AAEmu.Game.Services.Health;
using AAEmu.Game.Services.Security;

using NetCoreServer;

namespace AAEmu.UnitTests.Services.Health;

public class GameHealthSessionTests
{
    [Test]
    public async Task Live_WhenGameIsNotReady_ReturnsOk()
    {
        var healthState = new GameHealthState();
        using var server = new GameHealthServer(IPAddress.Loopback, 10000, healthState);
        using var session = new GameHealthSessionFake(server, healthState);

        session.Receive(new HttpRequest("GET", "/health/live", "HTTP/1.1"));

        await Assert.That(session.CapturedResponse.Status).IsEqualTo(200);
        await Assert.That(session.CapturedResponse.Body).IsEqualTo("Live");
    }

    [Test]
    public async Task Ready_WhenStateChanges_ReflectsCurrentReadiness()
    {
        var healthState = new GameHealthState();
        using var server = new GameHealthServer(IPAddress.Loopback, 10000, healthState);
        using var session = new GameHealthSessionFake(server, healthState);

        session.Receive(new HttpRequest("GET", "/health/ready", "HTTP/1.1"));
        await Assert.That(session.CapturedResponse.Status).IsEqualTo(503);
        await Assert.That(session.CapturedResponse.Body).IsEqualTo("Not ready");

        healthState.MarkReady();
        session.Receive(new HttpRequest("GET", "/health/ready", "HTTP/1.1"));
        await Assert.That(session.CapturedResponse.Status).IsEqualTo(200);
        await Assert.That(session.CapturedResponse.Body).IsEqualTo("Ready");

        healthState.MarkNotReady();
        session.Receive(new HttpRequest("GET", "/health/ready", "HTTP/1.1"));
        await Assert.That(session.CapturedResponse.Status).IsEqualTo(503);
    }

    [Test]
    [Arguments("POST", "/health/live")]
    [Arguments("GET", "/health/live/extra")]
    [Arguments("GET", "/health/live?details=true")]
    [Arguments("GET", "/status")]
    [Arguments("POST", "/api/commands/shutdown")]
    [Arguments("POST", "/api/mail/send")]
    [Arguments("GET", "/api/auction/list")]
    [Arguments("GET", "/not-found")]
    [Arguments("POST", "/metrics")]
    [Arguments("GET", "/metrics?details=true")]
    [Arguments("GET", "/metrics/extra")]
    public async Task NonHealthRequest_ReturnsNotFound(string method, string path)
    {
        var healthState = new GameHealthState();
        healthState.MarkReady();
        using var server = new GameHealthServer(IPAddress.Loopback, 10000, healthState);
        using var session = new GameHealthSessionFake(server, healthState);

        session.Receive(new HttpRequest(method, path, "HTTP/1.1"));

        await Assert.That(session.CapturedResponse.Status).IsEqualTo(404);
        await Assert.That(session.CapturedResponse.Body).IsEqualTo("Not found");
    }

    [Test]
    public async Task Metrics_WhenGameIsNotReady_ReturnsCurrentCounters()
    {
        var healthState = new GameHealthState();
        var metrics = new GameSecurityMetrics();
        metrics.TokenFailure("stream", "expired");
        using var server = new GameHealthServer(IPAddress.Loopback, 10000, healthState, metrics);
        using var session = new GameHealthSessionFake(server, healthState, metrics);

        session.Receive(new HttpRequest("GET", "/metrics", "HTTP/1.1"));

        await Assert.That(session.CapturedResponse.Status).IsEqualTo(200);
        await Assert.That(session.CapturedResponse.Body).Contains("aaemu_game_token_failures_total{kind=\"stream\",reason=\"expired\"} 1");
        var headers = Encoding.UTF8.GetString(session.CapturedResponse.Cache.AsSpan()).Split("\r\n\r\n", 2)[0];
        var contentType = headers.Split("\r\n").Single(header => header.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
        await Assert.That(contentType).IsEqualTo("Content-Type: text/plain; version=0.0.4; charset=utf-8");
    }

    private sealed class GameHealthSessionFake(HttpServer server, GameHealthState healthState, GameSecurityMetrics metrics = null)
        : GameHealthSession(server, healthState, metrics)
    {
        public HttpResponse CapturedResponse { get; private set; }

        public void Receive(HttpRequest request)
        {
            OnReceivedRequest(request);
        }

        protected override void SendHealthResponseAsync(HttpResponse response)
        {
            CapturedResponse = response;
        }
    }
}
