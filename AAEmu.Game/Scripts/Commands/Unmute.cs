using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ModerateAccounts)]
public class Unmute : ModerationCommand
{
    public override string[] CommandNames { get; set; } = ["unmute"];
    public override ModerationAction Action => ModerationAction.Unmute;
}
