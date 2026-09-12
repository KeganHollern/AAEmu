using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.CashShop;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class CashShopPurchaseTests
{
    [Test]
    [Arguments(CashShopCurrencyType.Credits)]
    [Arguments(CashShopCurrencyType.Loyalty)]
    [Arguments(CashShopCurrencyType.Coins)]
    public async Task Cart_UsesDiscountForEveryCurrencyAndSumsUnitQuantities(CashShopCurrencyType currency)
    {
        var (manager, item, sku) = Shop();
        sku.Currency = currency;
        var result = manager.TryPlanPurchase(new CharacterMock { Level = 50 }, false, [1u, 1u], DateTime.UtcNow, out var plan);
        await Assert.That(result).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(plan.Costs[(int)currency]).IsEqualTo(14);
        await Assert.That(plan.Quantities[item]).IsEqualTo(6);
    }

    [Test]
    [Arguments("currency")]
    [Arguments("overflow")]
    [Arguments("stock")]
    [Arguments("limit")]
    [Arguments("missing")]
    [Arguments("zero-quantity")]
    [Arguments("hidden")]
    [Arguments("gift")]
    public async Task InvalidCart_RejectsEveryLineWithoutStockMutation(string invalid)
    {
        var (manager, item, sku) = Shop();
        switch (invalid)
        {
            case "currency": sku.Currency = CashShopCurrencyType.AaPoints; break;
            case "overflow": sku.DiscountPrice = int.MaxValue; break;
            case "stock": item.Remaining = 5; break;
            case "limit": item.LimitedType = CashShopLimitType.Account; item.LimitedStockMax = 5; break;
            case "missing": manager.SKUs.Remove(1); break;
            case "zero-quantity": sku.ItemCount = 0; break;
            case "hidden": item.IsHidden = true; break;
            case "gift": item.ShopButtons = CashShopCmdUiType.NoGiftAllowed; break;
        }
        var stock = item.Remaining;
        var result = manager.TryPlanPurchase(new CharacterMock { Level = 50 }, invalid == "gift", [1u, 1u], DateTime.UtcNow, out var plan);
        await Assert.That(result == ErrorMessageType.NoErrorMessage).IsFalse();
        await Assert.That(plan).IsNull();
        await Assert.That(item.Remaining).IsEqualTo(stock);
    }

    [Test]
    public async Task LegacyQuantity_RemainsExhaustedAfterSkuDeletionOrChange()
    {
        await Assert.That(CashShopManager.CountPurchasedUnits([new AuditIcsSale { Sku = 999, ItemCount = null }])).IsEqualTo(uint.MaxValue);
        await Assert.That(CashShopManager.CountPurchasedUnits([new AuditIcsSale { Sku = 999, ItemCount = 3 }])).IsEqualTo(3u);
    }

    private static (CashShopManager, IcsItem, IcsSku) Shop()
    {
        var manager = new CashShopManager(Mock.Of<IWorldManager>().Object, Mock.Of<IAccountManager>().Object,
            Mock.Of<ILocalizationManager>().Object);
        var sku = new IcsSku { Sku = 1, ShopId = 2, ItemId = 100, ItemCount = 3, Price = 10, DiscountPrice = 7 };
        var item = new IcsItem { ShopId = 2, Remaining = 10, Skus = { [1] = sku } };
        manager.SKUs[1] = sku;
        manager.ShopItems[2] = item;
        return (manager, item, sku);
    }
}
