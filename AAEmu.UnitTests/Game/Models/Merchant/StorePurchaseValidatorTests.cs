using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Merchant;

namespace AAEmu.UnitTests.Game.Models.Merchant;

public class StorePurchaseValidatorTests
{
    [Test]
    public async Task MerchantKindSelectsAuthoritativeCurrency()
    {
        await Assert.That(new MerchantGoods(1, 0).Currency).IsEqualTo(ShopCurrencyType.Money);
        await Assert.That(new MerchantGoods(1, 1).Currency).IsEqualTo(ShopCurrencyType.Honor);
        await Assert.That(new MerchantGoods(1, 3).Currency).IsEqualTo(ShopCurrencyType.VocationBadges);
        await Assert.That(new MerchantGoods(1, 2).Currency).IsNull();
    }

    [Test]
    public async Task PlanUsesPackGradeAndTemplatePrice()
    {
        var pack = new MerchantGoods(RemoteShopCatalog.HonorPackId, 1);
        pack.AddItemToStock(24750, 2);
        var templates = new Dictionary<uint, ItemTemplate>
        {
            [24750] = new() { Id = 24750, HonorPrice = 400 }
        };

        var success = StorePurchaseValidator.TryCreatePlan(
            pack,
            [new StorePurchaseRequest(24750, 3, ShopCurrencyType.Honor)],
            templates.GetValueOrDefault,
            out var plan,
            out var error);

        await Assert.That(success).IsTrue();
        await Assert.That(error).IsEqualTo(StorePurchaseError.None);
        await Assert.That(plan.Cost.Honor).IsEqualTo(1200);
        await Assert.That(plan.Items[0]).IsEqualTo(new StorePurchaseItem(24750, 2, 3));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task PlanRejectsNonPositiveCounts(int count)
    {
        var (success, error) = CreateHonorPlan(
            [new StorePurchaseRequest(24750, count, ShopCurrencyType.Honor)]);

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsEqualTo(StorePurchaseError.InvalidItem);
    }

    [Test]
    public async Task PlanRejectsCurrencyChosenByClient()
    {
        var (success, error) = CreateHonorPlan(
            [new StorePurchaseRequest(24750, 1, ShopCurrencyType.Money)]);

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsEqualTo(StorePurchaseError.InvalidCurrency);
    }

    [Test]
    public async Task PlanRejectsItemsOutsideMerchantPack()
    {
        var (success, error) = CreateHonorPlan(
            [new StorePurchaseRequest(99999, 1, ShopCurrencyType.Honor)]);

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsEqualTo(StorePurchaseError.InvalidItem);
    }

    [Test]
    public async Task PlanRejectsDuplicateLines()
    {
        var request = new StorePurchaseRequest(24750, 1, ShopCurrencyType.Honor);
        var (success, error) = CreateHonorPlan([request, request]);

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsEqualTo(StorePurchaseError.DuplicateItem);
    }

    [Test]
    public async Task PlanRejectsCostOverflow()
    {
        var (success, error) = CreateHonorPlan(
            [new StorePurchaseRequest(24750, int.MaxValue, ShopCurrencyType.Honor)]);

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsEqualTo(StorePurchaseError.CostOverflow);
    }

    [Test]
    public async Task EachBalanceIsValidatedIndependently()
    {
        var honorError = StorePurchaseValidator.ValidateBalances(
            new StorePurchaseCost(0, 400, 0), 1000, 399, 1000);
        var vocationError = StorePurchaseValidator.ValidateBalances(
            new StorePurchaseCost(0, 0, 400), 1000, 1000, 399);
        var moneyError = StorePurchaseValidator.ValidateBalances(
            new StorePurchaseCost(400, 0, 0), 399, 1000, 1000);

        await Assert.That(honorError).IsEqualTo(StorePurchaseError.NotEnoughHonor);
        await Assert.That(vocationError).IsEqualTo(StorePurchaseError.NotEnoughVocationBadges);
        await Assert.That(moneyError).IsEqualTo(StorePurchaseError.NotEnoughMoney);
    }

    [Test]
    public async Task RemoteShopSessionExpiresAndRefreshes()
    {
        var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var session = new RemoteShopSession(RemoteShopCatalog.HonorPackId, now.AddSeconds(1));

        await Assert.That(session.IsValid(now)).IsTrue();
        await Assert.That(session.IsValid(now.AddSeconds(1))).IsFalse();
        await Assert.That(session.Refresh(now).ExpiresAtUtc).IsEqualTo(now + RemoteShopSession.Lifetime);
    }

    [Test]
    public async Task RemoteShopNamesMapOnlyToCataloguedPacks()
    {
        var honor = RemoteShopCatalog.TryGetPackId("honor", out var honorPack);
        var vocation = RemoteShopCatalog.TryGetPackId("VOCATION", out var vocationPack);
        var unknown = RemoteShopCatalog.TryGetPackId("money", out var unknownPack);

        await Assert.That(honor).IsTrue();
        await Assert.That(honorPack).IsEqualTo(RemoteShopCatalog.HonorPackId);
        await Assert.That(vocation).IsTrue();
        await Assert.That(vocationPack).IsEqualTo(RemoteShopCatalog.VocationPackId);
        await Assert.That(unknown).IsFalse();
        await Assert.That(unknownPack).IsEqualTo(0u);
    }

    private static (bool Success, StorePurchaseError Error) CreateHonorPlan(
        IReadOnlyList<StorePurchaseRequest> requests)
    {
        var pack = new MerchantGoods(RemoteShopCatalog.HonorPackId, 1);
        pack.AddItemToStock(24750, 2);
        var template = new ItemTemplate { Id = 24750, HonorPrice = 400 };
        var success = StorePurchaseValidator.TryCreatePlan(
            pack,
            requests,
            itemId => itemId == template.Id ? template : null,
            out _,
            out var error);
        return (success, error);
    }
}
