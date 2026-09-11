using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ModerateAccounts)]
public class Kick : ICommand
{
    public string[] CommandNames { get; set; } = ["kick_player", "kick"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<character name|id|account:id> <reason>";
    }

    public string GetCommandHelpText()
    {
        return "Saves and disconnects the target account. Only Admin can moderate staff accounts.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        ExecuteCore(character, args, messageOutput, ModerationManager.Instance);
    }

    internal void ExecuteCore(Character character, string[] args, IMessageOutput messageOutput, IModerationManager manager)
    {
        if (args.Length < 2)
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        if (!manager.TryResolveTarget(args[0], out var target))
        {
            CommandManager.SendErrorText(this, messageOutput, "The target account was not found.");
            return;
        }
        var reason = string.Join(' ', args.Skip(1));
        if (!new ModerationRequest(1, 1, 1, 1, ModerationAction.Ban, 0, reason).IsValid())
        {
            CommandManager.SendErrorText(this, messageOutput, "Use a reason of 1 to 512 UTF-8 bytes without control characters.");
            return;
        }
        CommandAuditContext.RecordTarget(target.AccountId, target.CharacterId);
        if (!manager.TryKick(character, target, reason, out var error))
            CommandManager.SendErrorText(this, messageOutput, error);
        else
            messageOutput.SendMessage($"Account {target.AccountId} was disconnected.");
    }
}
