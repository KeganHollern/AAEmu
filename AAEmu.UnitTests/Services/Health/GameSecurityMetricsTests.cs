using AAEmu.Game.Services.Security;

namespace AAEmu.UnitTests.Services.Health;

public class GameSecurityMetricsTests
{
    [Test]
    public async Task Counters_ExportPrometheusValuesAndStartAtZero()
    {
        var metrics = new GameSecurityMetrics();
        await Assert.That(metrics.Collect()).Contains("aaemu_game_token_failures_total{kind=\"world_cookie\",reason=\"expired\"} 0");

        metrics.TokenFailure("world_cookie", "expired");
        metrics.PacketRejected("game", "unknown_opcode");
        metrics.Disconnected("stream", "authentication_timeout");
        metrics.SuspiciousActivity("cheat", "queued");
        metrics.CommandOutcome("game_chat", "completed");

        var text = metrics.Collect();
        await Assert.That(text).Contains("# TYPE aaemu_game_token_failures_total counter");
        await Assert.That(text).Contains("aaemu_game_token_failures_total{kind=\"world_cookie\",reason=\"expired\"} 1");
        await Assert.That(text).Contains("aaemu_game_packets_rejected_total{network=\"game\",reason=\"unknown_opcode\"} 1");
        await Assert.That(text).Contains("aaemu_game_disconnects_total{network=\"stream\",reason=\"authentication_timeout\"} 1");
        await Assert.That(text).Contains("aaemu_game_suspicious_events_total{category=\"cheat\",outcome=\"queued\"} 1");
        await Assert.That(text).Contains("aaemu_game_gm_commands_total{source=\"game_chat\",outcome=\"completed\"} 1");
    }

    [Test]
    public async Task UntrustedLabels_CannotCreateSeriesOrLeakValues()
    {
        var metrics = new GameSecurityMetrics();
        var initialLines = metrics.Collect().Split('\n').Length;
        for (var index = 0; index < 1000; index++)
        {
            var untrusted = $"secret-{index}\"\nattacker";
            metrics.TokenFailure(untrusted, untrusted);
            metrics.PacketRejected(untrusted, untrusted);
            metrics.Disconnected(untrusted, untrusted);
            metrics.SuspiciousActivity(untrusted, untrusted);
            metrics.CommandOutcome(untrusted, untrusted);
        }

        var text = metrics.Collect();
        await Assert.That(text.Split('\n').Length).IsEqualTo(initialLines);
        await Assert.That(text).DoesNotContain("secret-");
        await Assert.That(text).Contains("aaemu_game_token_failures_total{kind=\"other\",reason=\"other\"} 1000");
    }

    [Test]
    public async Task ParallelIncrements_AreExactAndRegistriesAreIndependent()
    {
        var metrics = new GameSecurityMetrics();
        var other = new GameSecurityMetrics();

        Parallel.For(0, 10000, _ => metrics.PacketRejected("game", "rate_limit"));

        await Assert.That(metrics.Collect()).Contains("aaemu_game_packets_rejected_total{network=\"game\",reason=\"rate_limit\"} 10000");
        await Assert.That(other.Collect()).Contains("aaemu_game_packets_rejected_total{network=\"game\",reason=\"rate_limit\"} 0");
    }
}
