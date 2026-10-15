using System.Text;

using Prometheus;

namespace AAEmu.Game.Services.Security;

/// <summary>Process counters with bounded labels. Credentials and player identifiers never become labels.</summary>
public sealed class GameSecurityMetrics
{
    private static readonly string[] Networks = ["game", "stream", "login", "other"];
    private static readonly string[] TokenKinds = ["world_cookie", "stream", "reconnect", "other"];
    private static readonly string[] TokenReasons =
    [
        "not_found", "account_mismatch", "expired", "address_mismatch", "address_blocked",
        "invalid_state", "admission_denied", "invalid_credentials", "invalid_session", "raced", "other"
    ];
    private static readonly string[] PacketReasons =
        ["unknown_opcode", "invalid_state", "rate_limit", "malformed", "handler_error", "other"];
    private static readonly string[] DisconnectReasons =
    [
        "remote_close", "server_close", "server_shutdown", "admission_limit", "authentication_timeout",
        "authentication_rejected", "invalid_state", "rate_limit", "protocol_error", "duplicate_account",
        "moderation", "other"
    ];
    private static readonly string[] SuspiciousCategories = ["bot", "bot_report", "cheat", "chat_spam", "rmt", "other"];
    private static readonly string[] SuspiciousOutcomes = ["queued", "queue_full", "stopped", "invalid", "other"];
    private static readonly string[] CommandSources = ["game_chat", "web_api", "other"];
    private static readonly string[] CommandOutcomes =
        ["completed", "denied", "rejected", "failed", "audit_unavailable", "audit_finish_failed", "other"];

    public static GameSecurityMetrics Default { get; } = new();

    private readonly CollectorRegistry _registry;
    private readonly Counter _tokenFailures;
    private readonly Counter _packetRejections;
    private readonly Counter _disconnects;
    private readonly Counter _suspiciousEvents;
    private readonly Counter _commands;

    public GameSecurityMetrics()
    {
        _registry = Metrics.NewCustomRegistry();
        var factory = Metrics.WithCustomRegistry(_registry);
        _tokenFailures = Create(factory, "aaemu_game_token_failures_total", "Rejected Game authentication tokens.",
            "kind", TokenKinds, "reason", TokenReasons);
        _packetRejections = Create(factory, "aaemu_game_packets_rejected_total", "Rejected Game server packets.",
            "network", Networks, "reason", PacketReasons);
        _disconnects = Create(factory, "aaemu_game_disconnects_total", "Closed Game server connections by their first close reason.",
            "network", Networks, "reason", DisconnectReasons);
        _suspiciousEvents = Create(factory, "aaemu_game_suspicious_events_total", "Suspicious activity reports and queue outcomes.",
            "category", SuspiciousCategories, "outcome", SuspiciousOutcomes);
        _commands = Create(factory, "aaemu_game_gm_commands_total", "Completed GM command audit outcomes.",
            "source", CommandSources, "outcome", CommandOutcomes);
    }

    public void TokenFailure(string kind, string reason) =>
        _tokenFailures.WithLabels(Normalize(kind, TokenKinds), Normalize(reason, TokenReasons)).Inc();

    public void PacketRejected(string network, string reason) =>
        _packetRejections.WithLabels(Normalize(network, Networks), Normalize(reason, PacketReasons)).Inc();

    public void Disconnected(string network, string reason) =>
        _disconnects.WithLabels(Normalize(network, Networks), Normalize(reason, DisconnectReasons)).Inc();

    public void SuspiciousActivity(string category, string outcome) =>
        _suspiciousEvents.WithLabels(Normalize(category, SuspiciousCategories), Normalize(outcome, SuspiciousOutcomes)).Inc();

    public void CommandOutcome(string source, string outcome) =>
        _commands.WithLabels(Normalize(source, CommandSources), Normalize(outcome, CommandOutcomes)).Inc();

    internal string Collect()
    {
        using var stream = new MemoryStream();
        // This private registry has only in-memory counters and no collection callbacks.
        _registry.CollectAndExportAsTextAsync(stream).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static string Normalize(string value, string[] allowed) =>
        Array.IndexOf(allowed, value) >= 0 ? value : "other";

    private static Counter Create(MetricFactory factory, string name, string help,
        string firstLabel, string[] firstValues, string secondLabel, string[] secondValues)
    {
        var counter = factory.CreateCounter(name, help, new CounterConfiguration { LabelNames = [firstLabel, secondLabel] });
        // Publish zeroes before the first event so changes can be observed from the first scrape.
        foreach (var first in firstValues)
        foreach (var second in secondValues)
            counter.WithLabels(first, second).Inc(0);
        return counter;
    }
}
