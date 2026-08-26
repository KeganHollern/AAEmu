using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.StaticValues;
using NLog;

namespace AAEmu.Game.Models.Game.Merchant;

public static class StorePurchaseExecutor
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public static void Execute(
        Character character,
        MerchantGoods pack,
        StorePurchasePlan plan,
        IReadOnlyList<int> buyBackIndices,
        bool remotePurchase,
        bool hasNpc)
    {
        var buyBackItems = new Dictionary<Item, int>();
        long buyBackCost = 0;
        if (buyBackIndices.Count > 0)
        {
            if (remotePurchase || !hasNpc || pack.Currency != ShopCurrencyType.Money)
            {
                character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
                return;
            }

            foreach (var index in buyBackIndices)
            {
                var item = character.BuyBackItems.GetItemBySlot(index);
                if (item == null || !buyBackItems.TryAdd(item, index))
                {
                    character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
                    return;
                }

                var grade = ItemManager.Instance.GetGradeTemplate(item.Grade);
                if (grade == null)
                {
                    character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
                    return;
                }

                buyBackCost += (long)(item.Template.Refund * grade.RefundMultiplier / 100f) * item.Count;
                if (buyBackCost > int.MaxValue)
                {
                    character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
                    return;
                }
            }
        }

        if (plan == null && buyBackItems.Count == 0)
            return;

        var planCost = plan?.Cost ?? default;
        var totalMoney = (long)planCost.Money + buyBackCost;
        if (totalMoney > int.MaxValue)
        {
            character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
            return;
        }

        var balanceError = StorePurchaseValidator.ValidateBalances(
            planCost with { Money = (int)totalMoney },
            character.Money,
            character.HonorPoint,
            character.VocationPoint);
        if (balanceError != StorePurchaseError.None)
        {
            SendPurchaseError(character, balanceError);
            return;
        }

        if (!HasInventorySpace(character, plan?.Items ?? [], buyBackItems.Keys))
        {
            character.SendErrorMessage(ErrorMessageType.BagFull);
            return;
        }

        var tasks = new List<ItemTask>();
        var acquisitions = new List<ItemAcquisition>();
        var deferredSyncPackets = new List<GamePacket>();
        var transaction = new StorePurchaseTransaction(character, ItemManager.Instance.ReleaseId);
        var transactionResult = transaction.Execute(
            () => TryGrantItems(
                character,
                plan?.Items ?? [],
                buyBackItems.Keys,
                tasks,
                acquisitions,
                deferredSyncPackets),
            () => TrySpendCurrency(character, planCost, (int)totalMoney),
            out var failure);

        switch (transactionResult)
        {
            case StorePurchaseTransactionResult.InventoryFailed:
                character.SendErrorMessage(ErrorMessageType.BagFull);
                return;
            case StorePurchaseTransactionResult.CurrencyFailed:
                SendPurchaseError(character, GetInsufficientCurrencyError(planCost, (int)totalMoney));
                return;
            case StorePurchaseTransactionResult.UnexpectedFailure:
                Logger.Error(failure, $"Store purchase transaction failed for character {character.Id}");
                character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
                return;
        }

        character.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.StoreBuy, tasks, []));
        foreach (var packet in deferredSyncPackets)
            character.SendPacket(packet);
        foreach (var acquisition in acquisitions)
            character.Inventory.OnAcquiredItem(acquisition.Item, acquisition.Count, acquisition.OnlyUpdatedCount);
    }

    private static bool TryGrantItems(
        Character character,
        IReadOnlyList<StorePurchaseItem> purchaseItems,
        IEnumerable<Item> buyBackItems,
        List<ItemTask> tasks,
        List<ItemAcquisition> acquisitions,
        List<GamePacket> deferredSyncPackets)
    {
        var bag = character.Inventory.Bag;
        foreach (var purchaseItem in purchaseItems)
        {
            var previousCounts = bag.Items.ToDictionary(item => item, item => item.Count);
            if (!bag.AcquireDefaultItemEx(
                    ItemTaskType.Invalid,
                    purchaseItem.ItemId,
                    purchaseItem.Count,
                    purchaseItem.Grade,
                    out var newItems,
                    out var updatedItems,
                    0,
                    -1,
                    false,
                    deferredSyncPackets))
                return false;

            foreach (var item in updatedItems)
            {
                var addedCount = item.Count - previousCounts.GetValueOrDefault(item);
                tasks.Add(new ItemCountUpdate(item, addedCount));
                acquisitions.Add(new ItemAcquisition(item, addedCount, true));
            }

            foreach (var item in newItems)
            {
                tasks.Add(new ItemAdd(item));
                acquisitions.Add(new ItemAcquisition(item, item.Count, false));
            }
        }

        foreach (var item in buyBackItems)
        {
            if (!bag.AddOrMoveExistingItem(ItemTaskType.Invalid, item, -1, false))
                return false;
            tasks.Add(new ItemBuyback(item));
            acquisitions.Add(new ItemAcquisition(item, item.Count, false));
        }

        return true;
    }

    private static bool TrySpendCurrency(
        Character character,
        StorePurchaseCost planCost,
        int totalMoney)
    {
        if (planCost.Honor > 0)
            return character.TrySpendGamePoints(GamePointKind.Honor, planCost.Honor);
        if (planCost.VocationBadges > 0)
            return character.TrySpendGamePoints(GamePointKind.Vocation, planCost.VocationBadges);
        if (totalMoney > 0)
        {
            return character.ChangeMoney(
                SlotType.Inventory,
                SlotType.None,
                totalMoney,
                ItemTaskType.StoreBuy);
        }
        return true;
    }

    private static bool HasInventorySpace(
        Character character,
        IReadOnlyList<StorePurchaseItem> purchaseItems,
        IEnumerable<Item> buyBackItems)
    {
        var bag = character.Inventory.Bag;
        long requiredSlots = buyBackItems.LongCount();

        foreach (var purchaseItem in purchaseItems)
        {
            var template = ItemManager.Instance.GetTemplate(purchaseItem.ItemId);
            if (template == null || template.MaxCount <= 0)
                return false;

            bag.GetAllItemsByTemplate(
                purchaseItem.ItemId,
                purchaseItem.Grade,
                out var currentItems,
                out var currentCount);
            var existingCapacity = (long)currentItems.Count * template.MaxCount - currentCount;
            var unitsNeedingSlots = Math.Max(0L, (long)purchaseItem.Count - existingCapacity);
            requiredSlots += (unitsNeedingSlots + template.MaxCount - 1) / template.MaxCount;
            if (requiredSlots > bag.FreeSlotCount)
                return false;
        }

        return requiredSlots <= bag.FreeSlotCount;
    }

    private static void SendPurchaseError(Character character, StorePurchaseError error)
    {
        var message = error switch
        {
            StorePurchaseError.NotEnoughMoney => ErrorMessageType.NotEnoughMoney,
            StorePurchaseError.NotEnoughHonor => ErrorMessageType.NotEnoughHonorPoint,
            StorePurchaseError.NotEnoughVocationBadges => ErrorMessageType.NotEnoughLivingPoint,
            _ => ErrorMessageType.StoreInvalidItem
        };
        character.SendErrorMessage(message);
    }

    private static StorePurchaseError GetInsufficientCurrencyError(
        StorePurchaseCost cost,
        int totalMoney)
    {
        if (cost.Honor > 0)
            return StorePurchaseError.NotEnoughHonor;
        if (cost.VocationBadges > 0)
            return StorePurchaseError.NotEnoughVocationBadges;
        if (totalMoney > 0)
            return StorePurchaseError.NotEnoughMoney;
        return StorePurchaseError.InvalidItem;
    }

    private sealed record ItemAcquisition(Item Item, int Count, bool OnlyUpdatedCount);
}
