using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ModerateAccounts)]
public class Ban : ModerationCommand
{
    public override string[] CommandNames { get; set; } = ["ban"];
    public override ModerationAction Action => ModerationAction.Ban;
}
