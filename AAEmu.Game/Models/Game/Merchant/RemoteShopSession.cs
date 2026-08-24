namespace AAEmu.Game.Models.Game.Merchant;

public static class RemoteShopCatalog
{
    public const uint HonorPackId = 192;
    public const uint VocationPackId = 164;

    public static bool TryGetPackId(string shopName, out uint merchantPackId)
    {
        merchantPackId = shopName?.ToLowerInvariant() switch
        {
            "honor" => HonorPackId,
            "vocation" => VocationPackId,
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
