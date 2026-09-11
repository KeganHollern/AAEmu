using System.Globalization;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public abstract class ModerationCommand : ICommand
{
    public abstract string[] CommandNames { get; set; }
    public abstract ModerationAction Action { get; }
    private bool HasDuration => Action is ModerationAction.Ban or ModerationAction.Mute;

    public void OnLoad() => CommandManager.Instance.Register(CommandNames, this);

    public string GetCommandLineHelp() => HasDuration
        ? "<character name|id|account:id> <permanent|30m|2h|7d> <reason>"
        : "<character name|id|account:id> <reason>";

    public string GetCommandHelpText() => $"{Action} the target account. Only Admin can moderate staff accounts.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
        => ExecuteCore(character, args, messageOutput, ModerationManager.Instance);

    internal void ExecuteCore(Character character, string[] args, IMessageOutput output, IModerationManager manager)
    {
        if (!TryParse(Action, args, out var duration, out var reason))
        {
            CommandManager.SendErrorText(this, output, $"Usage: {CommandNames[0]} {GetCommandLineHelp()}");
            return;
        }
        if (!manager.TryResolveTarget(args[0], out var target))
        {
            CommandManager.SendErrorText(this, output, "The target account was not found.");
            return;
        }

        CommandAuditContext.RecordTarget(target.AccountId, target.CharacterId);
        var complete = CommandAuditContext.DeferResult();
        output.SendMessage($"{Action} is pending for account {target.AccountId}.");
        _ = CompleteAsync(character, target, duration, reason, output, manager, complete);
    }

    internal async Task CompleteAsync(Character actor, ModerationTarget target, ulong duration, string reason,
        IMessageOutput output, IModerationManager manager, Action<string, string> complete)
    {
        var outcome = "unconfirmed";
        var detail = "Moderation failed.";
        try
        {
            var result = await manager.ModerateAsync(actor, target, Action, duration, reason);
            outcome = result.Status switch
            {
                ModerationStatus.Success => "completed",
                ModerationStatus.Unavailable => "unconfirmed",
                _ => "rejected"
            };
            detail = result.Status switch
            {
                ModerationStatus.Success => $"{Action} completed for account {target.AccountId}.",
                ModerationStatus.Unavailable => $"{Action} completion was not confirmed for account {target.AccountId}: Unavailable.",
                _ => $"{Action} failed for account {target.AccountId}: {result.Status}."
            };
            if (outcome is "completed" or "unconfirmed")
                output.SendMessage(detail);
            else
                CommandManager.SendErrorText(this, output, detail);
        }
        catch (Exception)
        {
            detail = $"{Action} completion was not confirmed. Check the server log.";
            output.SendMessage(detail);
        }
        finally
        {
            complete(outcome, detail);
        }
    }

    internal static bool TryParse(ModerationAction action, string[] args, out ulong duration, out string reason)
    {
        duration = 0;
        reason = string.Empty;
        var hasDuration = action is ModerationAction.Ban or ModerationAction.Mute;
        var reasonIndex = hasDuration ? 2 : 1;
        if (action == ModerationAction.ReadState || !Enum.IsDefined(action) || args.Length <= reasonIndex)
            return false;
        if (hasDuration && !TryParseDuration(args[1], out duration))
            return false;
        reason = string.Join(' ', args.Skip(reasonIndex));
        return new ModerationRequest(1, 1, 1, 1, action, duration, reason).IsValid();
    }

    internal static bool TryParseDuration(string value, out ulong seconds)
    {
        seconds = 0;
        if (value.Equals("permanent", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Length < 2 || !ulong.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var count) || count == 0)
            return false;
        var multiplier = char.ToLowerInvariant(value[^1]) switch
        {
            's' => 1ul,
            'm' => 60ul,
            'h' => 3600ul,
            'd' => 86400ul,
            _ => 0ul
        };
        if (multiplier == 0 || count > ModerationRequest.MaximumDurationSeconds / multiplier)
            return false;
        seconds = count * multiplier;
        return true;
    }
}
