using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.CashShop;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Core.Managers;

public partial class CashShopManager(IWorldManager worldManager, IAccountManager accountManager, ILocalizationManager localizationManager) : Singleton<CashShopManager>, ICashShopManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public bool Enabled { get; private set; }

    public Dictionary<uint, IcsSku> SKUs { get; set; } = [];
    public Dictionary<uint, IcsItem> ShopItems { get; set; } = [];
    public List<IcsMenu> MenuItems { get; set; } = [];

    public void CreditDisperseTick(TimeSpan delta)
    {
        var characters = worldManager.GetAllCharacters();

        foreach (var character in characters)
        {
            accountManager.AddCredits(character.AccountId, 100);
            character.SendMessage("You have received 100 credits.");
        }
    }

    public void Load()
    {
        SKUs.Clear();
        ShopItems.Clear();
        MenuItems.Clear();

        using var connection = MySQL.CreateConnection();

        // Load SKUs
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM ics_skus ORDER BY shop_id, position";
            command.Prepare();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = new IcsSku
                {
                    Sku = reader.GetUInt32("sku"), ShopId = reader.GetUInt32("shop_id"), Position = reader.GetInt32("position"),
                    ItemId = reader.GetUInt32("item_id"),
                    ItemCount = reader.GetUInt32("item_count"),
                    SelectType = reader.GetByte("select_type"),
                    IsDefault = reader.GetBoolean("is_default"),
                    EventType = reader.GetByte("event_type"),
                    EventEndDate = reader.IsDBNull(reader.GetOrdinal("event_end_date")) ? DateTime.MinValue : reader.GetDateTime("event_end_date"),
                    Currency = (CashShopCurrencyType)reader.GetByte("currency"),
                    Price = reader.GetUInt32("price"),
                    DiscountPrice = reader.GetUInt32("discount_price"),
                    BonusItemId = reader.GetUInt32("bonus_item_id"),
                    BonusItemCount = reader.GetUInt32("bonus_item_count")
                };

                if (!SKUs.TryAdd(entry.Sku, entry))
                    Logger.Error($"Duplicate SKU {entry.Sku}");
            }
        }

        // Load Shop Items
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM ics_shop_items";
            command.Prepare();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = new IcsItem
                {
                    ShopId = reader.GetUInt32("shop_id"),
                    DisplayItemId = reader.GetUInt32("display_item_id"),
                    Name = reader.IsDBNull(reader.GetOrdinal("name")) ? "" : reader.GetString("name"),
                    LimitedType = (CashShopLimitType)reader.GetByte("limited_type"),
                    LimitedStockMax = reader.GetUInt16("limited_stock_max"),
                    LevelMin = reader.GetByte("level_min"),
                    LevelMax = reader.GetByte("level_max"),
                    BuyRestrictType = (CashShopRestrictSaleType)reader.GetByte("buy_restrict_type"),
                    BuyRestrictId = reader.GetUInt32("buy_restrict_id"),
                    IsSale = reader.GetBoolean("is_sale"),
                    IsHidden = reader.GetBoolean("is_hidden"),
                    SaleStart = reader.IsDBNull(reader.GetOrdinal("sale_start")) ? DateTime.MinValue : reader.GetDateTime("sale_start"),
                    SaleEnd = reader.IsDBNull(reader.GetOrdinal("sale_end")) ? DateTime.MinValue : reader.GetDateTime("sale_end"),
                    Remaining = reader.GetInt32("remaining"),
                    ShopButtons = (CashShopCmdUiType)reader.GetByte("shop_buttons")
                };

                if (!ShopItems.TryAdd(entry.ShopId, entry))
                    Logger.Error($"Duplicate ShopItem {entry.ShopId}");
            }
        }

        // Attach SKUs to Shop Items
        foreach (var (key, sku) in SKUs)
        {
            if (ShopItems.TryGetValue(sku.ShopId, out var shopItem))
            {
                if (shopItem.Skus.Count <= 0 && string.IsNullOrWhiteSpace(shopItem.Name))
                {
                    // First Item, grab it's name when needed
                    shopItem.Name = localizationManager.Get("items", "name", sku.ItemId) ?? "???";
                }
                shopItem.Skus.Add(sku.Sku, sku);
            }
            else
            {
                Logger.Warn($"Found SKU without a valid Shop Item SKU: {key}, ShopItem: {sku.ShopId}");
            }
        }

        // Verify if all Shop Items have at least one SKU attached
        foreach (var (key, shopItem) in ShopItems)
        {
            if (shopItem.Skus.Count < 1)
                Logger.Error($"Shop Item found without any SKUs attached {key}");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM ics_menu ORDER BY main_tab, sub_tab, tab_pos";
            command.Prepare();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var shopItemId = reader.GetUInt32("shop_id");
                if (!ShopItems.TryGetValue(shopItemId, out var shopItem))
                {
                    Logger.Warn($"Menu Entry without a valid ShopId: {shopItemId}");
                    continue;
                }

                var entry = new IcsMenu
                {
                    Id = reader.GetInt64("id"), MainTab = reader.GetByte("main_tab"), SubTab = reader.GetByte("sub_tab"),
                    TabPos = reader.GetUInt16("tab_pos"),
                    ShopItem = shopItem
                };

                // Note that this List should technically always be in order by main, sub and position
                MenuItems.Add(entry);
            }
        }

        // If something didn't load, force close the shop
        if (MenuItems.Count <= 0 || ShopItems.Count <= 0 || SKUs.Count <= 0)
            DisableShop();
    }

    public void Initialize()
    {
        // TickManager.Instance.OnTick.Subscribe(CreditDisperseTick, TimeSpan.FromMinutes(5));
    }

    public void EnabledShop()
    {
        Enabled = true;
    }

    public void DisableShop()
    {
        Enabled = false;
        foreach (var character in worldManager.GetAllCharacters())
            character?.SendPacket(new SCICSCheckTimePacket());
    }

    public void SendICSPage(GameConnection connection, byte mainTabId, byte subTabId, ushort page)
    {
        var thisTabItems = MenuItems.Where(t => t.MainTab == mainTabId && t.SubTab == subTabId).ToList();
        var isLimitedTab = mainTabId == 1 && subTabId == 1;
        var itemsPerPage = isLimitedTab ? 4 : 8;
        var numberOfPages = (ushort)Math.Ceiling((float)thisTabItems.Count / itemsPerPage);
        var thisPageItems = thisTabItems.Skip(itemsPerPage * (page - 1)).Take(itemsPerPage).ToList();

        // Bundle the whole page into one compressed send: streaming each goods
        // row and SKU detail as its own packet made the client repaint the tab
        // incrementally (a visible blink on first render of every tab).
        var packets = new CompressedGamePackets();

        for (var i = 0; i < thisPageItems.Count; i++)
        {
            var isLast = i == thisPageItems.Count - 1;
            var shopItem = thisPageItems[i].ShopItem;
            if (shopItem == null)
                continue;

            int? remainingOverride = null;
            if (shopItem.LimitedType != CashShopLimitType.None && connection.ActiveChar != null)
            {
                // The protocol has no per-buyer purchase-count packet, so the
                // client cannot grey out an item whose account/character limit
                // this viewer already consumed. Mask the remaining count to 0
                // for them: "# Left" becomes "left for you" and the client
                // renders its sold-out state instead of a doomed buy flow.
                var bought = GetPurchasedCount(shopItem, connection.AccountId, connection.ActiveChar.Id);
                if (bought >= shopItem.LimitedStockMax)
                    remainingOverride = 0;
            }

            packets.AddPacket(new SCICSGoodListPacket(isLast, numberOfPages, mainTabId, subTabId, shopItem, remainingOverride));
        }

        for (var i = 0; i < thisPageItems.Count; i++)
        {
            var isLastItem = i >= thisPageItems.Count - 1;
            var shopItem = thisPageItems[i].ShopItem;
            if (shopItem == null)
                continue;

            var n = 0;
            foreach (var sku in shopItem.Skus.Values)
            {
                var isLastSku = n >= shopItem.Skus.Count - 1;
                packets.AddPacket(new SCICSGoodDetailPacket(isLastSku && isLastItem, sku));
                n++;
            }
        }

        if (packets.Packets.Count > 0)
            connection.SendPacket(packets);
    }

    /// <summary>
    /// Returns a list of sales for a specific ShopItem made by accountId or characterId
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="characterId"></param>
    /// <param name="shopItemId"></param>
    /// <returns>Resulting list of sales</returns>
    public List<AuditIcsSale> GetSalesForShopItem(uint accountId, uint characterId, uint shopItemId)
    {
        var res = new List<AuditIcsSale>();

        if ((accountId == 0 && characterId == 0) || shopItemId <= 0)
            return res;

        using var connection = MySQL.CreateConnection();

        // Load Sales
        using (var command = connection.CreateCommand())
        {
            if (characterId > 0)
            {
                command.CommandText = "SELECT * FROM audit_ics_sales WHERE (buyer_char = @char_id) AND (shop_item_id = @shop_id)";
                command.Parameters.AddWithValue("@char_id", characterId);
            }
            else
            {
                command.CommandText = "SELECT * FROM audit_ics_sales WHERE (buyer_account = @acc_id) AND (shop_item_id = @shop_id)";
                command.Parameters.AddWithValue("@acc_id", accountId);
            }
            command.Parameters.AddWithValue("@shop_id", shopItemId);
            command.Prepare();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = new AuditIcsSale
                {
                    BuyerAccount = reader.GetUInt32("buyer_account"),
                    BuyerChar = reader.GetUInt32("buyer_char"),
                    TargetAccount = reader.GetUInt32("target_account"),
                    TargetChar = reader.GetUInt32("target_char"),
                    SaleDate = reader.IsDBNull(reader.GetOrdinal("sale_date")) ? DateTime.MinValue : reader.GetDateTime("sale_date"),
                    ShopItemId = reader.GetUInt32("shop_item_id"),
                    Sku = reader.GetUInt32("sku"),
                    ItemCount = reader.IsDBNull(reader.GetOrdinal("item_count")) ? null : reader.GetUInt32("item_count"),
                    SaleCost = reader.GetInt32("sale_cost"),
                    SaleCurrency = (CashShopCurrencyType)reader.GetByte("sale_currency"),
                    Description = reader.GetString("description")
                };

                res.Add(entry);
            }
        }
        return res;
    }

    /// <summary>
    /// Returns how many units of a limited ShopItem the given account or
    /// character has already bought, across every SKU attached to it.
    /// </summary>
    public uint GetPurchasedCount(IcsItem shopItem, uint accountId, uint characterId)
    {
        if (shopItem.LimitedType == CashShopLimitType.None)
            return 0;

        var oldSales = GetSalesForShopItem(
            accountId,
            shopItem.LimitedType == CashShopLimitType.Character ? characterId : 0,
            shopItem.ShopId);

        return CountPurchasedUnits(oldSales);
    }

    internal static uint CountPurchasedUnits(IEnumerable<AuditIcsSale> sales)
    {
        ulong count = 0;
        foreach (var sale in sales)
        {
            // Historical quantities cannot be recovered from a mutable SKU catalog.
            if (!sale.ItemCount.HasValue)
                return uint.MaxValue;
            count += sale.ItemCount.Value;
            if (count >= uint.MaxValue)
                return uint.MaxValue;
        }
        return (uint)count;
    }

}
