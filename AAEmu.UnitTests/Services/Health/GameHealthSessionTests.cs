using System.Net;

using AAEmu.Game.Services.Health;

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

        await Assert.That(session.Response.Status).IsEqualTo(200);
        await Assert.That(session.Response.Body).IsEqualTo("Live");
    }

    [Test]
    public async Task Ready_WhenStateChanges_ReflectsCurrentReadiness()
    {
        var healthState = new GameHealthState();
        using var server = new GameHealthServer(IPAddress.Loopback, 10000, healthState);
        using var session = new GameHealthSessionFake(server, healthState);

        session.Receive(new HttpRequest("GET", "/health/ready", "HTTP/1.1"));
        await Assert.That(session.Response.Status).IsEqualTo(503);
        await Assert.That(session.Response.Body).IsEqualTo("Not ready");

        healthState.MarkReady();
        session.Receive(new HttpRequest("GET", "/health/ready", "HTTP/1.1"));
        await Assert.That(session.Response.Status).IsEqualTo(200);
        await Assert.That(session.Response.Body).IsEqualTo("Ready");

        healthState.MarkNotReady();
        session.Receive(new HttpRequest("GET", "/health/ready", "HTTP/1.1"));
        await Assert.That(session.Response.Status).IsEqualTo(503);
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
    public async Task NonHealthRequest_ReturnsNotFound(string method, string path)
    {
        var healthState = new GameHealthState();
        healthState.MarkReady();
        using var server = new GameHealthServer(IPAddress.Loopback, 10000, healthState);
        using var session = new GameHealthSessionFake(server, healthState);

        session.Receive(new HttpRequest(method, path, "HTTP/1.1"));

        await Assert.That(session.Response.Status).IsEqualTo(404);
        await Assert.That(session.Response.Body).IsEqualTo("Not found");
    }

    private sealed class GameHealthSessionFake(HttpServer server, GameHealthState healthState)
        : GameHealthSession(server, healthState)
    {
        public HttpResponse Response { get; private set; }

        public void Receive(HttpRequest request)
        {
            OnReceivedRequest(request);
        }

        protected override void SendHealthResponseAsync(HttpResponse response)
        {
            Response = response;
        }
    }
}
