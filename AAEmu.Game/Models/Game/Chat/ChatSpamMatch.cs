namespace AAEmu.Game.Models.Game.Chat;

public sealed record ChatSpamMatch(
    uint RuleId,
    string RuleName,
    uint DetailId,
    string DetailText);
