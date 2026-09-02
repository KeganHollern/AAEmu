namespace AAEmu.Game.Models.Game.Chat;

public sealed class ChatSpamRule
{
    private readonly Dictionary<uint, ChatSpamRuleDetail> _details = [];

    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsValid { get; internal set; } = true;
    public IReadOnlyDictionary<uint, ChatSpamRuleDetail> Details => _details;

    internal ChatSpamRuleDetail StartNode { get; set; }
    internal Dictionary<uint, ChatSpamRuleDetail> MutableDetails => _details;
}
