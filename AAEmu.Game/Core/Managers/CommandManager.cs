using System.Drawing;
using System.Text;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;
using NLog;

namespace AAEmu.Game.Core.Managers;

public class CommandManager(IPermissionManager permissions, ICommandAuditStore auditStore)
    : Singleton<CommandManager>, ICommandManager
{
    public const string CommandPrefix = "/";
    public const int MaximumCommandLength = 4096;
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly object _sync = new();
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _commandAliases = new(StringComparer.OrdinalIgnoreCase);

    public List<string> GetCommandKeys()
    {
        lock (_sync)
            return _commands.Keys.ToList();
    }

    public ICommand GetCommandInterfaceByName(string commandName)
    {
        lock (_sync)
        {
            var canonical = _commandAliases.GetValueOrDefault(commandName ?? "") ?? commandName ?? "";
            return _commands.GetValueOrDefault(canonical);
        }
    }

    public string GetCommandNameBase(string aliasName)
    {
        lock (_sync)
        {
            if (_commandAliases.TryGetValue(aliasName ?? "", out var canonical))
                return canonical;
            return _commands.ContainsKey(aliasName ?? "") ? aliasName.ToLowerInvariant() : "";
        }
    }

    public string UnAliasCommandName(string command)
    {
        var canonical = GetCommandNameBase(command);
        return canonical.Length > 0 ? canonical : command;
    }

    public void Register(string name, ICommand command)
    {
        Register([name], command);
    }

    public void Register(string[] names, ICommand command)
    {
        if (names.Length == 0 || CommandPermissions.GetDeclaration(command) == null)
            throw new InvalidOperationException("Every command needs names and an explicit permission.");

        var normalized = names.Select(name => name.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Any(name => string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException("Command names cannot be empty or contain whitespace.");

        lock (_sync)
        {
            foreach (var name in normalized)
            {
                if (_commands.ContainsKey(name) || _commandAliases.ContainsKey(name))
                    throw new InvalidOperationException($"Duplicate command or alias: {name}");
            }

            _commands.Add(normalized[0], command);
            foreach (var alias in normalized.Skip(1))
                _commandAliases.Add(alias, normalized[0]);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _commands.Clear();
            _commandAliases.Clear();
        }
    }

    public IEnumerable<string> GetVisibleCommands(AccountRole role)
    {
        lock (_sync)
            return _commands.Where(pair => CommandPermissions.GetDeclaration(pair.Value)?.ShowInHelp == true &&
                    CommandPermissions.Allows(role, pair.Value, []))
                .Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryGetHelp(AccountRole role, string[] path, out string help)
    {
        help = "";
        if (path.Length == 0)
            return false;
        var command = GetCommandInterfaceByName(path[0]);
        if (command == null || !CommandPermissions.Allows(role, command, path.Skip(1).ToArray()))
            return false;

        var canonical = GetCommandNameBase(path[0]);
        var current = command as SubCommandBase;
        var resolved = new List<string> { canonical };
        foreach (var part in path.Skip(1))
        {
            if (current == null || !current.TryResolveChild(part, out var child, out var childName))
                return false;
            resolved.Add(childName);
            current = child as SubCommandBase;
        }

        help = $"Help for {CommandPrefix}{string.Join(" ", resolved)}\n" +
            (path.Length > 1 && current != null ? current.GetVisibleHelp(role) : CommandPermissions.GetHelp(command, role));
        return true;
    }

    internal static bool TrySplitCommand(string text, out string[] words)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (char.IsControl(value) && value != '\t')
            {
                words = [];
                return false;
            }
            if (value == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (quoted && value == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
            {
                token.Append(text[++index]);
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(value))
            {
                if (!started)
                    continue;
                tokens.Add(token.ToString());
                token.Clear();
                started = false;
            }
            else
            {
                token.Append(value);
                started = true;
            }
        }

        if (started)
            tokens.Add(token.ToString());
        words = tokens.ToArray();
        return !quoted;
    }

    public bool Handle(Character character, string text, out IMessageOutput messageOutput)
    {
        messageOutput = new CharacterMessageOutput(character);
        return Handle(character, text, messageOutput);
    }

    internal bool Handle(Character character, string text, IMessageOutput messageOutput)
    {
        text = (text ?? "").Replace("@@", "@").Replace("||", "|");
        var validLength = text.Length <= MaximumCommandLength;
        var parsed = TrySplitCommand(CommandAuditContext.Bound(text, MaximumCommandLength), out var words);
        var name = words.FirstOrDefault()?.ToLowerInvariant() ?? "";
        var canonical = GetCommandNameBase(name);
        var args = words.Skip(1).ToArray();
        var role = permissions.GetRole(character);
        var entry = new CommandAuditEntry
        {
            ActorAccountId = character.AccountId,
            ActorCharacterId = character.Id,
            ActorRole = role,
            RemoteAddress = character.Connection?.Ip?.ToString() ?? "",
            Command = CommandAuditContext.Bound(canonical.Length > 0 ? canonical : name, 128),
            Arguments = parsed && validLength ? args : [CommandAuditContext.Bound(text, MaximumCommandLength)]
        };
        entry.Targets.Add(new CommandAuditTarget(character.AccountId, character.Id, character.ObjId, "actor"));
        if (character.CurrentTarget is { } target)
        {
            var targetCharacter = target as Character;
            entry.Targets.Add(new CommandAuditTarget(targetCharacter?.AccountId ?? 0,
                targetCharacter?.Id ?? 0, target.ObjId, "selected"));
        }

        if (!TryStartAudit(entry, messageOutput))
            return true;

        using var audit = new CommandAuditContext(entry, auditStore);
        if (!validLength || !parsed || words.Length == 0)
        {
            messageOutput.SendMessage("Invalid command text or length.");
            audit.Complete("rejected", "Invalid command text or length.");
            return true;
        }

        var forceReload = GetCommandKeys().Count == 0 && words.Length == 3 &&
            name == "scripts" && words[1].Equals("reload", StringComparison.OrdinalIgnoreCase) &&
            words[2].Equals("force", StringComparison.OrdinalIgnoreCase);
        var command = GetCommandInterfaceByName(name);
        var permitted = forceReload
            ? PermissionPolicy.Allows(role, GamePermission.ManageServer)
            : command != null && CommandPermissions.Allows(role, command, args);

        if (!permitted)
        {
            messageOutput.SendMessage("Command unavailable or permission denied.");
            audit.Complete("denied", "Command unavailable or permission denied.");
            return true;
        }

        try
        {
            if (forceReload)
            {
                Clear();
                if (!ScriptCompiler.Compile())
                    CommandAuditContext.Fail("Script reload failed.");
                messageOutput.SendMessage("Script reload finished. Check the server log.");
            }
            else if (command is ICommandV2 subcommand)
            {
                subcommand.PreExecute(character, canonical, args, messageOutput);
            }
            else
            {
                command.Execute(character, args, messageOutput);
            }

            if (!audit.IsDeferred)
                audit.Complete();
        }
        catch (Exception exception)
        {
            audit.Complete("failed", $"Command exception: {exception.GetType().Name}");
            Logger.Error(exception, "Command {CommandName} failed, request {RequestId}.", canonical, entry.RequestId);
            messageOutput.SendMessage("Command failed. See the server log.");
        }

        return true;
    }

    private bool TryStartAudit(CommandAuditEntry entry, IMessageOutput output = null)
    {
        try
        {
            auditStore.Start(entry);
            return true;
        }
        catch (Exception exception)
        {
            entry.CompletedAt = DateTime.UtcNow;
            entry.Result = "audit-unavailable";
            entry.Detail = $"No command ran: {exception.GetType().Name}";
            CommandAuditContext.WriteEvent(entry);
            output?.SendMessage("Command audit is unavailable. No command ran.");
            return false;
        }
    }

    public void RejectWebApiCommand(string address, string name, string body)
    {
        var entry = new CommandAuditEntry
        {
            Source = "web-api",
            RemoteAddress = CommandAuditContext.Bound(address, 64),
            Command = CommandAuditContext.Bound(name, 128),
            Arguments = [CommandAuditContext.Bound(body, MaximumCommandLength)]
        };
        if (!TryStartAudit(entry))
            return;
        using var audit = new CommandAuditContext(entry, auditStore);
        audit.Complete("denied", "The Web API cannot authenticate a staff actor. Use the authenticated game command path.");
    }

    public static void SendDefaultHelpText(ICommand command, IMessageOutput messageOutput)
    {
        var role = CommandAuditContext.CurrentRole ?? AccountRole.NormalPlayer;
        messageOutput.SendMessage(ChatType.System,
            $"Help for {CommandPrefix}{command.CommandNames.FirstOrDefault()}\n{CommandPermissions.GetHelp(command, role)}");
    }

    public static void SendErrorText(ICommand command, IMessageOutput messageOutput, string errorDetails)
    {
        CommandAuditContext.Fail(errorDetails);
        messageOutput.SendMessage(ChatType.System,
            $"[{command.CommandNames.FirstOrDefault() ?? "Invalid Command"}] {errorDetails}", Color.Red);
    }

    public static void SendNormalText(ICommand command, IMessageOutput messageOutput, string text)
    {
        messageOutput.SendMessage(ChatType.System, $"[{command.CommandNames.FirstOrDefault() ?? "Invalid Command"}] {text}");
    }
}
