using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.CashShop;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public partial class CashShopManager
{
    internal Func<IReadOnlyCollection<Character>, Action<PersistenceSaveContext>, bool> CommitPurchase { get; set; } =
        (participants, write) => SaveManager.Instance.TryCommitEconomy(participants, write);

    public ErrorMessageType Purchase(Character buyer, uint receiverId, uint receiverAccountId,
        string receiverName, IReadOnlyList<uint> skuIds)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!Enabled || buyer?.Connection?.ActiveChar != buyer || !buyer.IsOnline ||
                buyer.AccountId == 0 || receiverId == 0 || receiverAccountId == 0 ||
                NameManager.Instance.GetCharacterAccount(receiverId) != receiverAccountId ||
                !string.Equals(NameManager.Instance.GetCharacterName(receiverId), receiverName, StringComparison.OrdinalIgnoreCase))
                return ErrorMessageType.IngameShopBuyFail;

            var result = TryPlanPurchase(buyer, receiverId != buyer.Id, skuIds, DateTime.UtcNow, out var plan);
            if (result != ErrorMessageType.NoErrorMessage)
                return result;
            return SettlePurchase(buyer, receiverId, receiverAccountId, receiverName, plan);
        }
    }

    internal ErrorMessageType TryPlanPurchase(Character buyer, bool gift, IReadOnlyList<uint> skuIds,
        DateTime now, out PurchasePlan plan)
    {
        plan = null;
        if (buyer == null || skuIds == null || skuIds.Count is < 1 or > byte.MaxValue)
            return ErrorMessageType.BuyCartEmpty;
        var lines = new List<PurchaseLine>();
        var costs = new int[(int)CashShopCurrencyType.Max];
        var quantities = new Dictionary<IcsItem, int>();
        try
        {
            foreach (var skuId in skuIds)
            {
                if (!SKUs.TryGetValue(skuId, out var sku) || !ShopItems.TryGetValue(sku.ShopId, out var item) ||
                    !item.Skus.TryGetValue(skuId, out var offeredSku) || !ReferenceEquals(sku, offeredSku) ||
                    item.IsHidden || item.ShopButtons > CashShopCmdUiType.OnlyBuyAllowed ||
                    (gift && item.ShopButtons is CashShopCmdUiType.NoGiftAllowed or CashShopCmdUiType.OnlyBuyAllowed) ||
                    item.BuyRestrictType > CashShopRestrictSaleType.Quest || item.LimitedType > CashShopLimitType.Character ||
                    sku.Currency is not (CashShopCurrencyType.Credits or CashShopCurrencyType.Loyalty or CashShopCurrencyType.Coins) ||
                    sku.ItemCount == 0 || sku.ItemCount > int.MaxValue || sku.BonusItemCount > int.MaxValue ||
                    (sku.BonusItemId == 0) != (sku.BonusItemCount == 0))
                    return ErrorMessageType.IngameShopBuyFail;
                if ((sku.EventEndDate > DateTime.MinValue && now >= sku.EventEndDate) ||
                    (item.SaleStart > DateTime.MinValue && now < item.SaleStart) ||
                    (item.SaleEnd > DateTime.MinValue && now >= item.SaleEnd))
                    return ErrorMessageType.IngameShopExpiredSellByDate;
                if ((item.LevelMin > 0 && buyer.Level < item.LevelMin) ||
                    (item.LevelMax > 0 && buyer.Level > item.LevelMax) ||
                    (item.BuyRestrictType == CashShopRestrictSaleType.Level && buyer.Level < item.BuyRestrictId))
                    return ErrorMessageType.IngameShopBuyLowLevel;
                if (item.BuyRestrictType == CashShopRestrictSaleType.Quest &&
                    (buyer.Quests == null || !buyer.Quests.HasQuestCompleted(item.BuyRestrictId)))
                    return ErrorMessageType.IngameShopBuyQuestIncomplete;

                var price = checked((int)(sku.DiscountPrice > 0 ? sku.DiscountPrice : sku.Price));
                costs[(int)sku.Currency] = checked(costs[(int)sku.Currency] + price);
                quantities.TryGetValue(item, out var quantity);
                quantities[item] = checked(quantity + (int)sku.ItemCount);
                lines.Add(new PurchaseLine(sku.Sku, sku.ShopId, sku.ItemId, (int)sku.ItemCount,
                    sku.BonusItemId, (int)sku.BonusItemCount, sku.Currency, price, item.Name ?? string.Empty));
            }
        }
        catch (OverflowException)
        {
            return ErrorMessageType.IngameShopBuyFail;
        }
        foreach (var (item, quantity) in quantities)
            if ((item.Remaining >= 0 && item.Remaining < quantity) ||
                (item.LimitedType != CashShopLimitType.None && quantity > item.LimitedStockMax))
                return ErrorMessageType.IngameShopSoldOut;
        plan = new PurchasePlan(lines, costs, quantities, now);
        return ErrorMessageType.NoErrorMessage;
    }

    internal ErrorMessageType SettlePurchase(Character buyer, uint receiverId, uint receiverAccountId,
        string receiverName, PurchasePlan plan)
    {
        using var inventory = new InventoryMutation(ItemTaskType.StoreBuy);
        using var mails = MailManager.Instance.BeginMutation();
        if (plan.Costs[(int)CashShopCurrencyType.Coins] > 0 &&
            !inventory.TryChangeMoney(buyer, -plan.Costs[(int)CashShopCurrencyType.Coins]))
            return ErrorMessageType.NotEnoughCoin;

        var container = ItemManager.Instance.GetItemContainerForCharacter(receiverId, SlotType.Mail, null, 0);
        if (container == null)
            return ErrorMessageType.IngameShopBuyFail;
        foreach (var line in plan.Lines)
        {
            if (!inventory.TryGrant(container, line.ItemId, line.Quantity, out var purchased))
                return ErrorMessageType.IngameShopBuyFail;
            var attachments = purchased.ToList();
            if (line.BonusItemId > 0)
            {
                if (!inventory.TryGrant(container, line.BonusItemId, line.BonusQuantity, out var bonus))
                    return ErrorMessageType.IngameShopBuyFail;
                attachments.AddRange(bonus);
            }
            // Each mail retains exact attachment IDs, including split stacks and bonus items.
            foreach (var batch in attachments.Chunk(MailBody.MaxMailAttachments))
            {
                var mail = new CommercialMail(receiverId, receiverName, buyer.Name, [.. batch],
                    receiverId != buyer.Id, false, line.Name);
                mail.PrepareContent();
                if (!mails.TryAdd(mail))
                    return ErrorMessageType.IngameShopBuyFail;
            }
        }

        var stock = plan.Quantities.Keys.ToDictionary(item => item, item => item.Remaining);
        foreach (var (item, quantity) in plan.Quantities)
            if (item.Remaining >= 0)
                item.Remaining -= quantity;
        var failure = ErrorMessageType.IngameShopBuyFail;
        bool committed;
        try
        {
            committed = CommitPurchase([buyer], context =>
            {
                try
                {
                    WritePurchase(context, buyer, receiverId, receiverAccountId, plan);
                }
                catch (PurchaseRejectedException rejection)
                {
                    failure = rejection.Error;
                    throw;
                }
            });
        }
        catch
        {
            // A lost commit acknowledgement is not a known rollback. Stop new carts until restart.
            inventory.PreservePreparedState();
            mails.PreservePreparedState();
            DisableShop();
            throw;
        }
        if (!committed)
        {
            foreach (var (item, remaining) in stock)
                item.Remaining = remaining;
            return failure;
        }
        try
        {
            inventory.Complete();
            mails.Complete();
        }
        catch
        {
            inventory.PreservePreparedState();
            mails.PreservePreparedState();
            throw;
        }
        foreach (var item in plan.Quantities.Keys)
        {
            var remaining = item.Remaining;
            if (item.LimitedType != CashShopLimitType.None &&
                GetPurchasedCount(item, buyer.AccountId, buyer.Id) >= item.LimitedStockMax)
                remaining = 0;
            buyer.SendPacket(new SCICSSyncGoodPacket((int)item.ShopId, remaining));
        }
        var details = accountManager.GetAccountDetails(buyer.AccountId);
        buyer.BmPoint = details.Loyalty;
        buyer.SendPacket(new SCICSCashPointPacket(details.Credits));
        buyer.SendPacket(new SCBmPointPacket(details.Loyalty));
        return ErrorMessageType.NoErrorMessage;
    }

    internal static void WritePurchase(PersistenceSaveContext context, Character buyer, uint receiverId,
        uint receiverAccountId, PurchasePlan plan)
    {
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        // Lock offers in a stable order before counting historical purchases.
        foreach (var (item, quantity) in plan.Quantities.OrderBy(entry => entry.Key.ShopId))
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT `remaining` FROM `ics_shop_items` WHERE `shop_id` = @shop FOR UPDATE";
            command.Parameters.AddWithValue("@shop", item.ShopId);
            var stored = command.ExecuteScalar();
            if (stored == null || (Convert.ToInt32(stored) >= 0 && Convert.ToInt32(stored) < quantity))
                throw new PurchaseRejectedException(ErrorMessageType.IngameShopSoldOut);
            if (item.LimitedType != CashShopLimitType.None)
            {
                var ownerColumn = item.LimitedType == CashShopLimitType.Character ? "buyer_char" : "buyer_account";
                command.CommandText = $"SELECT CASE WHEN COUNT(*) <> COUNT(`item_count`) THEN 4294967295 ELSE COALESCE(SUM(`item_count`), 0) END FROM `audit_ics_sales` WHERE `{ownerColumn}` = @owner AND `shop_item_id` = @shop";
                command.Parameters.AddWithValue("@owner", item.LimitedType == CashShopLimitType.Character ? buyer.Id : buyer.AccountId);
                if (Convert.ToUInt64(command.ExecuteScalar()) + (ulong)quantity > item.LimitedStockMax)
                    throw new PurchaseRejectedException(ErrorMessageType.IngameShopSoldOut);
            }
            command.CommandText = "UPDATE `ics_shop_items` SET `remaining` = `remaining` - @quantity WHERE `shop_id` = @shop AND `remaining` >= @quantity";
            command.Parameters.AddWithValue("@quantity", quantity);
            if (Convert.ToInt32(stored) >= 0 && command.ExecuteNonQuery() != 1)
                throw new PurchaseRejectedException(ErrorMessageType.IngameShopSoldOut);
        }
        var credits = plan.Costs[(int)CashShopCurrencyType.Credits];
        var loyalty = plan.Costs[(int)CashShopCurrencyType.Loyalty];
        command.Parameters.Clear();
        command.CommandText = "UPDATE `accounts` SET `credits` = `credits` - @credits, `loyalty` = `loyalty` - @loyalty WHERE `account_id` = @account AND `credits` >= @credits AND `loyalty` >= @loyalty";
        command.Parameters.AddWithValue("@account", buyer.AccountId);
        command.Parameters.AddWithValue("@credits", credits);
        command.Parameters.AddWithValue("@loyalty", loyalty);
        if (command.ExecuteNonQuery() != 1)
            throw new PurchaseRejectedException(ErrorMessageType.IngameShopNotEnoughAaCash);

        foreach (var line in plan.Lines)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO `audit_ics_sales` (`buyer_account`, `buyer_char`, `target_account`, `target_char`, `sale_date`, `shop_item_id`, `sku`, `item_count`, `sale_cost`, `sale_currency`, `description`) VALUES (@account, @buyer, @targetAccount, @target, @date, @shop, @sku, @quantity, @cost, @currency, '')";
            command.Parameters.AddWithValue("@account", buyer.AccountId);
            command.Parameters.AddWithValue("@buyer", buyer.Id);
            command.Parameters.AddWithValue("@targetAccount", receiverAccountId);
            command.Parameters.AddWithValue("@target", receiverId);
            command.Parameters.AddWithValue("@date", plan.Time);
            command.Parameters.AddWithValue("@shop", line.ShopId);
            command.Parameters.AddWithValue("@sku", line.SkuId);
            command.Parameters.AddWithValue("@quantity", line.Quantity);
            command.Parameters.AddWithValue("@cost", line.Cost);
            command.Parameters.AddWithValue("@currency", (byte)line.Currency);
            if (command.ExecuteNonQuery() != 1)
                throw new PurchaseRejectedException(ErrorMessageType.IngameShopBuyFail);
        }
    }

    internal sealed record PurchaseLine(uint SkuId, uint ShopId, uint ItemId, int Quantity,
        uint BonusItemId, int BonusQuantity, CashShopCurrencyType Currency, int Cost, string Name);
    internal sealed record PurchasePlan(IReadOnlyList<PurchaseLine> Lines, int[] Costs,
        IReadOnlyDictionary<IcsItem, int> Quantities, DateTime Time);
    private sealed class PurchaseRejectedException(ErrorMessageType error) : Exception("Cash shop purchase rejected")
    {
        public ErrorMessageType Error { get; } = error;
    }
}
