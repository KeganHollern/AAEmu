using System.Text.Json;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Account;
using NLog;

namespace AAEmu.Game.Utils.Scripts;

public sealed class CommandAuditContext : IDisposable
{
    private static readonly AsyncLocal<CommandAuditContext> s_current = new();
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly CommandAuditContext _previous;
    private readonly ICommandAuditStore _store;
    private readonly object _sync = new();
    private int _completed;
    private string _failure;

    public CommandAuditEntry Entry { get; }
    public bool IsDeferred { get; private set; }
    internal static AccountRole? CurrentRole => s_current.Value?.Entry.ActorRole;

    internal CommandAuditContext(CommandAuditEntry entry, ICommandAuditStore store)
    {
        Entry = entry;
        _store = store;
        _previous = s_current.Value;
        s_current.Value = this;
    }

    public static void RecordTarget(uint accountId, uint characterId = 0, uint objectId = 0)
    {
        var current = s_current.Value;
        if (current == null)
            return;

        var target = new CommandAuditTarget(accountId, characterId, objectId, "resolved");
        lock (current._sync)
        {
            if (!current.Entry.Targets.Contains(target))
                current.Entry.Targets.Add(target);
        }
    }

    public static void Fail(string detail)
    {
        var current = s_current.Value;
        if (current != null)
            current._failure = Bound(detail, 1024);
    }

    public static Action<bool, string> DeferCompletion()
    {
        var complete = DeferResult();
        return (success, detail) => complete(success ? "completed" : "rejected", detail);
    }

    public static Action<string, string> DeferResult()
    {
        var current = s_current.Value;
        if (current == null)
            return (_, _) => { };

        current.IsDeferred = true;
        return current.Complete;
    }

    public static void CompleteBeforeExit(string detail)
    {
        s_current.Value?.Complete("completed", detail);
    }

    internal void Complete(string result = "completed", string detail = "")
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        lock (_sync)
        {
            Entry.CompletedAt = DateTime.UtcNow;
            Entry.Result = _failure == null ? result : "rejected";
            Entry.Detail = Bound(_failure ?? detail, 1024);
            try
            {
                _store.Complete(Entry);
            }
            catch (Exception exception)
            {
                // The durable started row remains as evidence of an uncertain completion.
                Entry.Detail = Bound($"Audit completion failed: {exception.GetType().Name}; {Entry.Detail}", 1024);
                Entry.Result = "audit-finish-failed";
            }

            WriteEvent(Entry);
        }
    }

    internal static void WriteEvent(CommandAuditEntry entry)
    {
        Logger.Info(
            "{EventName}: Request={RequestId} ActorAccount={ActorAccountId} ActorCharacter={ActorCharacterId} " +
            "Role={ActorRole} Source={CommandSource} Address={RemoteAddress} Command={CommandName} " +
            "Arguments={CommandArguments} Targets={CommandTargets} Result={CommandResult} Detail={CommandDetail}",
            "staff.command", entry.RequestId, entry.ActorAccountId, entry.ActorCharacterId,
            entry.ActorRole, entry.Source, entry.RemoteAddress, entry.Command,
            JsonSerializer.Serialize(entry.Arguments), JsonSerializer.Serialize(entry.Targets),
            entry.Result, JsonSerializer.Serialize(entry.Detail));
    }

    internal static string Bound(string text, int length)
    {
        text ??= "";
        return text.Length <= length ? text : text[..length];
    }

    public void Dispose()
    {
        s_current.Value = _previous;
    }
}
