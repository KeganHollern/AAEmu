using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.CashShop;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Tasks.CashShop;

public class CashShopBuyTask(byte buyMode, Character buyer, uint targetId, uint targetAccountId,
    string targetName, List<IcsSku> shoppingCart) : Task
{
    private readonly uint[] _skuIds = shoppingCart.Select(sku => sku.Sku).ToArray();

    public override void Execute()
    {
        var result = CashShopManager.Instance.Purchase(buyer, targetId, targetAccountId, targetName, _skuIds);
        if (result != Game.ErrorMessageType.NoErrorMessage)
            buyer.SendErrorMessage(result);
        buyer.SendPacket(new SCICSBuyResultPacket(result == Game.ErrorMessageType.NoErrorMessage, buyMode, targetName, 0));
    }
}
