using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ModerateAccounts)]
public class Unban : ModerationCommand
{
    public override string[] CommandNames { get; set; } = ["unban"];
    public override ModerationAction Action => ModerationAction.Unban;
}
