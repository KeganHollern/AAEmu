using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ModerateAccounts)]
public class Mute : ModerationCommand
{
    public override string[] CommandNames { get; set; } = ["mute"];
    public override ModerationAction Action => ModerationAction.Mute;
}
