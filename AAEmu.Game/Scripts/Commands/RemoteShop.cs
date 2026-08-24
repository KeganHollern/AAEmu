using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class RemoteShop : ICommand
{
    public string[] CommandNames { get; set; } = ["aaemu_shop"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<honor|vocation>";

    public string GetCommandHelpText() => "Opens a Character Info currency shop.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 1 || !RemoteShopCatalog.TryGetPackId(args[0], out var merchantPackId))
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        character.ActiveRemoteShop = new RemoteShopSession(
            merchantPackId,
            DateTime.UtcNow + RemoteShopSession.Lifetime);
    }
}
