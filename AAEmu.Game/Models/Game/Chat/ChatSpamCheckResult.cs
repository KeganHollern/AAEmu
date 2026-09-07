namespace AAEmu.Game.Models.Game.Chat;

public enum ChatSpamViolationType
{
    None,
    Muted,
    Rule,
    RateLimit,
    RepeatedMessage
}

public sealed record ChatSpamCheckResult(
    ChatSpamViolationType Violation,
    ErrorMessageType ErrorMessage,
    DateTimeOffset MutedUntil,
    uint RuleId = 0,
    uint DetailId = 0)
{
    public bool IsAllowed => Violation == ChatSpamViolationType.None;

    public static ChatSpamCheckResult Allowed { get; } = new(
        ChatSpamViolationType.None,
        ErrorMessageType.NoErrorMessage,
        DateTimeOffset.MinValue);
}
