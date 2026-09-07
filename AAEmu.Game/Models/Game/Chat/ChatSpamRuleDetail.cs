namespace AAEmu.Game.Models.Game.Chat;

public sealed class ChatSpamRuleDetail
{
    public uint Id { get; init; }
    public uint ChatSpamRuleId { get; init; }
    public string Text { get; init; } = string.Empty;
    public uint DetectedCaseNextDetailId { get; init; }
    public uint NotDetectedCaseNextDetailId { get; init; }
    public bool IsStartNode { get; init; }
    public bool IsEndNode { get; init; }
}
