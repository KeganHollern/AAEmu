using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ManageRoles)]
public class Role : ICommand
{
    public string[] CommandNames { get; set; } = ["role"];

    public void OnLoad() => CommandManager.Instance.Register(CommandNames, this);
    public string GetCommandLineHelp() => "<account-id> [NormalPlayer|Moderator|Admin]";
    public string GetCommandHelpText() => "Reads or changes the account role. Only Admin can use this command.";

    public void Execute(Character character, string[] args, IMessageOutput output)
    {
        if (args.Length is < 1 or > 2 || !uint.TryParse(args[0], out var targetId) || targetId == 0)
        {
            CommandManager.SendErrorText(this, output, GetCommandLineHelp());
            return;
        }

        CommandAuditContext.RecordTarget(targetId);
        var manager = AccountManager.Instance;
        if (args.Length == 1)
        {
            if (!manager.TryGetAccountRole(targetId, out var current))
                CommandManager.SendErrorText(this, output, "The account role is unavailable.");
            else
                CommandManager.SendNormalText(this, output, $"Account {targetId}: {current}.");
            return;
        }

        if (!Enum.GetNames<AccountRole>().Contains(args[1], StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<AccountRole>(args[1], true, out var role))
        {
            CommandManager.SendErrorText(this, output, "Use NormalPlayer, Moderator, or Admin.");
            return;
        }

        var result = manager.SetAccountRole(character.AccountId, targetId, role);
        if (result.Status == AccountRoleChangeStatus.Rejected)
        {
            CommandManager.SendErrorText(this, output, result.Detail);
            return;
        }
        if (result.Status == AccountRoleChangeStatus.Unconfirmed)
        {
            output.SendMessage(result.Detail);
            CommandAuditContext.DeferResult()("unconfirmed", result.Detail);
            return;
        }

        CommandManager.SendNormalText(this, output, $"Account {targetId} now has role {role}.");
    }
}
