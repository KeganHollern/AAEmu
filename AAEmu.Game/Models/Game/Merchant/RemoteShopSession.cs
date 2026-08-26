using System.Globalization;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.Game.Models.Game.Merchant;

public static class RemoteShopCatalog
{
    public const uint HonorPackId = 192;
    public const uint VocationPackId = 164;

    public static bool TryGetPackId(string shopName, out uint merchantPackId)
    {
        return TryGetPack(shopName, out merchantPackId, out _);
    }

    public static bool TryGetPack(
        string shopName,
        out uint merchantPackId,
        out ShopCurrencyType currency)
    {
        (merchantPackId, currency) = shopName?.ToLowerInvariant() switch
        {
            "honor" => (HonorPackId, ShopCurrencyType.Honor),
            "vocation" => (VocationPackId, ShopCurrencyType.VocationBadges),
            _ => (0, default)
        };
        return merchantPackId != 0;
    }

    public static bool TryParsePurchaseRequests(
        string encodedRequests,
        ShopCurrencyType currency,
        out IReadOnlyList<StorePurchaseRequest> requests)
    {
        requests = [];
        if (currency is not (ShopCurrencyType.Honor or ShopCurrencyType.VocationBadges) ||
            string.IsNullOrEmpty(encodedRequests))
            return false;

        var encodedLines = encodedRequests.Split(',');
        if (encodedLines.Length == 0 || encodedLines.Length > StorePurchaseValidator.MaxPurchaseLines)
            return false;

        var parsed = new List<StorePurchaseRequest>(encodedLines.Length);
        foreach (var encodedLine in encodedLines)
        {
            var fields = encodedLine.Split(':');
            if (fields.Length != 2 ||
                !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                itemId == 0 || count <= 0)
                return false;
            parsed.Add(new StorePurchaseRequest(itemId, count, currency));
        }

        requests = parsed;
        return true;
    }

    public static bool TryGetPackId(
        IReadOnlyList<StorePurchaseRequest> requests,
        out uint merchantPackId)
    {
        merchantPackId = 0;
        if (requests is not { Count: > 0 })
            return false;

        var currency = requests[0].Currency;
        if (requests.Any(request => request.Currency != currency))
            return false;

        merchantPackId = currency switch
        {
            ShopCurrencyType.Honor => HonorPackId,
            ShopCurrencyType.VocationBadges => VocationPackId,
            _ => 0
        };
        return merchantPackId != 0;
    }

    public static bool IsRemotePack(uint merchantPackId) =>
        merchantPackId is HonorPackId or VocationPackId;
}

public sealed record RemoteShopSession(uint MerchantPackId, DateTime ExpiresAtUtc)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public bool IsValid(DateTime utcNow) => MerchantPackId != 0 && utcNow < ExpiresAtUtc;

    public RemoteShopSession Refresh(DateTime utcNow) => this with { ExpiresAtUtc = utcNow + Lifetime };
}
