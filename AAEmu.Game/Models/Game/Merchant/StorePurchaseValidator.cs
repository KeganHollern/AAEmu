using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Merchant;

public readonly record struct StorePurchaseRequest(uint ItemId, int Count, ShopCurrencyType Currency);

public readonly record struct StorePurchaseItem(uint ItemId, byte Grade, int Count);

public readonly record struct StorePurchaseCost(int Money, int Honor, int VocationBadges);

public sealed record StorePurchasePlan(IReadOnlyList<StorePurchaseItem> Items, StorePurchaseCost Cost);

public enum StorePurchaseError
{
    None,
    InvalidShop,
    InvalidLineCount,
    InvalidItem,
    DuplicateItem,
    InvalidCurrency,
    InvalidPrice,
    CostOverflow,
    NotEnoughMoney,
    NotEnoughHonor,
    NotEnoughVocationBadges
}

public static class StorePurchaseValidator
{
    public const int MaxPurchaseLines = 10;

    public static bool TryCreatePlan(
        MerchantGoods pack,
        IReadOnlyList<StorePurchaseRequest> requests,
        Func<uint, ItemTemplate> templateResolver,
        out StorePurchasePlan plan,
        out StorePurchaseError error)
    {
        plan = null;
        error = StorePurchaseError.None;

        if (pack?.Currency is not { } expectedCurrency)
        {
            error = StorePurchaseError.InvalidShop;
            return false;
        }

        if (requests is not { Count: > 0 } || requests.Count > MaxPurchaseLines)
        {
            error = StorePurchaseError.InvalidLineCount;
            return false;
        }

        var seenItems = new HashSet<uint>();
        var items = new List<StorePurchaseItem>(requests.Count);
        long money = 0;
        long honor = 0;
        long vocationBadges = 0;

        foreach (var request in requests)
        {
            if (request.ItemId == 0 || request.Count <= 0 ||
                !pack.TryGetItem(request.ItemId, out var goodsItem))
            {
                error = StorePurchaseError.InvalidItem;
                return false;
            }

            if (!seenItems.Add(request.ItemId))
            {
                error = StorePurchaseError.DuplicateItem;
                return false;
            }

            if (request.Currency != expectedCurrency)
            {
                error = StorePurchaseError.InvalidCurrency;
                return false;
            }

            var template = templateResolver(request.ItemId);
            if (template == null)
            {
                error = StorePurchaseError.InvalidItem;
                return false;
            }

            var unitPrice = expectedCurrency switch
            {
                ShopCurrencyType.Money => template.Price,
                ShopCurrencyType.Honor => template.HonorPrice,
                ShopCurrencyType.VocationBadges => template.LivingPointPrice,
                _ => 0
            };
            if (unitPrice <= 0)
            {
                error = StorePurchaseError.InvalidPrice;
                return false;
            }

            var lineCost = (long)unitPrice * request.Count;
            switch (expectedCurrency)
            {
                case ShopCurrencyType.Money:
                    money += lineCost;
                    break;
                case ShopCurrencyType.Honor:
                    honor += lineCost;
                    break;
                case ShopCurrencyType.VocationBadges:
                    vocationBadges += lineCost;
                    break;
            }

            if (money > int.MaxValue || honor > int.MaxValue || vocationBadges > int.MaxValue)
            {
                error = StorePurchaseError.CostOverflow;
                return false;
            }

            items.Add(new StorePurchaseItem(request.ItemId, goodsItem.Grade, request.Count));
        }

        plan = new StorePurchasePlan(
            items,
            new StorePurchaseCost((int)money, (int)honor, (int)vocationBadges));
        return true;
    }

    public static StorePurchaseError ValidateBalances(
        StorePurchaseCost cost,
        long money,
        int honor,
        int vocationBadges)
    {
        if (cost.Money > money)
            return StorePurchaseError.NotEnoughMoney;
        if (cost.Honor > honor)
            return StorePurchaseError.NotEnoughHonor;
        if (cost.VocationBadges > vocationBadges)
            return StorePurchaseError.NotEnoughVocationBadges;
        return StorePurchaseError.None;
    }
}
