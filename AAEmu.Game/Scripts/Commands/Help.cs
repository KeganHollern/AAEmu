using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.PlayerCommands)]
public class Help : ICommand
{
    public string[] CommandNames { get; set; } = ["help", "?"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[command] [subcommand]";
    }

    public string GetCommandHelpText()
    {
        return "Lists commands available to your account role, or shows help for one command.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var role = PermissionManager.Instance.GetRole(character);
        var manager = CommandManager.Instance;
        if (args.Length > 0)
        {
            if (manager.TryGetHelp(role, args, out var help))
                messageOutput.SendMessage(help);
            else
                CommandManager.SendErrorText(this, messageOutput, "Command help is unavailable.");
            return;
        }

        messageOutput.SendMessage($"Account role: {role}. Available commands:");
        foreach (var name in manager.GetVisibleCommands(role))
        {
            var command = manager.GetCommandInterfaceByName(name);
            var children = command is AAEmu.Game.Utils.Scripts.SubCommands.SubCommandBase parent
                ? $" <{string.Join("|", parent.GetVisibleChildren(role))}>" : "";
            messageOutput.SendMessage($"{CommandManager.CommandPrefix}{name}{children}");
        }
    }
}
