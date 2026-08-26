using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
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

    public string GetCommandLineHelp() =>
        "<honor|vocation>|buy <honor|vocation> <itemId:count,...>";

    public string GetCommandHelpText() => "Opens a Character Info currency shop.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 1 && RemoteShopCatalog.TryGetPackId(args[0], out var merchantPackId))
        {
            character.ActiveRemoteShop = new RemoteShopSession(
                merchantPackId,
                DateTime.UtcNow + RemoteShopSession.Lifetime);
            return;
        }

        if (args.Length != 3 || !args[0].Equals("buy", StringComparison.OrdinalIgnoreCase) ||
            !RemoteShopCatalog.TryGetPack(args[1], out merchantPackId, out var currency) ||
            !RemoteShopCatalog.TryParsePurchaseRequests(args[2], currency, out var requests))
        {
            character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
            return;
        }

        var pack = NpcManager.Instance.GetGoods(merchantPackId);
        if (pack == null)
        {
            character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
            return;
        }

        if (!StorePurchaseValidator.TryCreatePlan(
                pack,
                requests,
                ItemManager.Instance.GetTemplate,
                out var plan,
                out var validationError))
        {
            character.SendErrorMessage(validationError switch
            {
                StorePurchaseError.NotEnoughMoney => ErrorMessageType.NotEnoughMoney,
                StorePurchaseError.NotEnoughHonor => ErrorMessageType.NotEnoughHonorPoint,
                StorePurchaseError.NotEnoughVocationBadges => ErrorMessageType.NotEnoughLivingPoint,
                _ => ErrorMessageType.StoreInvalidItem
            });
            return;
        }

        lock (character.StorePurchaseSyncRoot)
        {
            StorePurchaseExecutor.Execute(
                character,
                pack,
                plan,
                [],
                true,
                false);
        }
    }
}
