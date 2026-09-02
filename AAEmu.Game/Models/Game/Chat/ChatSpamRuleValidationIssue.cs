namespace AAEmu.Game.Models.Game.Chat;

public sealed record ChatSpamRuleValidationIssue(
    uint? RuleId,
    uint? DetailId,
    string Message);
