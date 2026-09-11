using AAEmu.Game.Models.Account;

namespace AAEmu.Game.Utils.Scripts;

public sealed class CommandAuditEntry
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public uint ActorAccountId { get; init; }
    public uint ActorCharacterId { get; init; }
    public AccountRole ActorRole { get; init; }
    public string Source { get; init; } = "game-chat";
    public string RemoteAddress { get; init; } = "";
    public string Command { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public List<CommandAuditTarget> Targets { get; } = [];
    public string Result { get; set; } = "started";
    public string Detail { get; set; } = "";
}

public sealed record CommandAuditTarget(uint AccountId, uint CharacterId, uint ObjectId, string Source);
