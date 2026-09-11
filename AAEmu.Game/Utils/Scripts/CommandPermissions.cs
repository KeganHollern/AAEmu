using System.Reflection;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Utils.Scripts;

public static class CommandPermissions
{
    public static CommandPermissionAttribute GetDeclaration(object command)
    {
        return command.GetType().GetCustomAttribute<CommandPermissionAttribute>();
    }

    public static bool AllowsDeclared(AccountRole role, object command, bool allowInherited = false)
    {
        var declaration = GetDeclaration(command);
        return declaration == null
            ? allowInherited
            : declaration.Enabled && PermissionPolicy.Allows(role, declaration.Permission);
    }

    public static bool Allows(AccountRole role, ICommand command, string[] arguments)
    {
        if (!AllowsDeclared(role, command))
            return false;

        var action = arguments.FirstOrDefault() ?? "";
        foreach (var restriction in command.GetType().GetCustomAttributes<CommandActionPermissionAttribute>())
        {
            if (arguments.Length > 0 &&
                (restriction.Actions.Length == 0 || restriction.Actions.Contains(action, StringComparer.OrdinalIgnoreCase)) &&
                !PermissionPolicy.Allows(role, restriction.Permission))
                return false;
        }

        var current = command as SubCommandBase;
        foreach (var argument in arguments)
        {
            if (current == null || !current.TryResolveChild(argument, out var child, out _))
                break;
            if (!AllowsDeclared(role, child, allowInherited: true))
                return false;
            current = child as SubCommandBase;
        }

        return true;
    }

    public static string GetHelp(ICommand command, AccountRole role)
    {
        if (command is SubCommandBase subcommand)
            return subcommand.GetVisibleHelp(role);

        var result = $"{command.GetCommandLineHelp()}\n{command.GetCommandHelpText()}";
        var restrictions = command.GetType().GetCustomAttributes<CommandActionPermissionAttribute>()
            .Where(restriction => !PermissionPolicy.Allows(role, restriction.Permission))
            .SelectMany(restriction => restriction.Actions.Length == 0 ? ["all arguments"] : restriction.Actions).ToArray();
        if (restrictions.Length > 0)
            result += $"\nAdmin-only actions: {string.Join(", ", restrictions)}";
        return result;
    }
}
