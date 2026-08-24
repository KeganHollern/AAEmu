using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSBuyItemsPacket() : GamePacket(CSOffsets.CSBuyItemsPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var character = Connection.ActiveChar;
        var npcObjId = stream.ReadBc();
        var npc = npcObjId == 0 ? null : character.ParentWorld.GetNpc(npcObjId);
        var doodadObjId = stream.ReadBc();
        var doodad = doodadObjId == 0 ? null : character.ParentWorld.GetDoodad(doodadObjId);
        var shopId = stream.ReadUInt32();
        var buyCount = stream.ReadByte();
        var buyBackCount = stream.ReadByte();

        var requests = new List<StorePurchaseRequest>(buyCount);
        for (var i = 0; i < buyCount; i++)
        {
            var itemId = stream.ReadUInt32();
            _ = stream.ReadByte(); // The merchant pack, not the client, owns the item grade.
            var count = stream.ReadInt32();
            var currency = (ShopCurrencyType)stream.ReadByte();
            requests.Add(new StorePurchaseRequest(itemId, count, currency));
        }

        var buyBackIndices = new List<int>(buyBackCount);
        for (var i = 0; i < buyBackCount; i++)
            buyBackIndices.Add(stream.ReadInt32());

        var useAaPoint = stream.ReadBoolean();
        Logger.Debug(
            $"NPCObjId:{npcObjId} DoodadObjId:{doodadObjId} ShopId:{shopId} " +
            $"BuyCount:{buyCount} BuyBackCount:{buyBackCount}");

        if (useAaPoint || buyCount > StorePurchaseValidator.MaxPurchaseLines ||
            buyBackCount > StorePurchaseValidator.MaxPurchaseLines)
        {
            character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
            return;
        }

        var now = DateTime.UtcNow;
        var remotePurchase = false;
        MerchantGoods pack;

        if (npcObjId != 0)
        {
            if (doodadObjId != 0 || npc == null || !npc.Template.Merchant ||
                npc.Template.MerchantPackId == 0 || !IsNear(character, npc))
                return;
            pack = NpcManager.Instance.GetGoods(npc.Template.MerchantPackId);
        }
        else if (doodadObjId != 0)
        {
            if (doodad == null || RemoteShopCatalog.IsRemotePack(shopId) ||
                !IsNear(character, doodad))
                return;
            pack = NpcManager.Instance.GetGoods(shopId);
        }
        else
        {
            var session = character.ActiveRemoteShop;
            if (session == null || !session.IsValid(now))
            {
                character.ActiveRemoteShop = null;
                character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
                return;
            }

            remotePurchase = true;
            pack = NpcManager.Instance.GetGoods(session.MerchantPackId);
        }

        if (pack == null)
        {
            character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
            return;
        }

        StorePurchasePlan plan = null;
        if (requests.Count > 0 &&
            !StorePurchaseValidator.TryCreatePlan(
                pack,
                requests,
                ItemManager.Instance.GetTemplate,
                out plan,
                out var validationError))
        {
            SendPurchaseError(validationError);
            return;
        }

        lock (character.StorePurchaseSyncRoot)
        {
            ExecutePurchase(
                character,
                pack,
                plan,
                buyBackIndices,
                remotePurchase,
                npc != null,
                now);
        }
    }

    private void ExecutePurchase(
        Character character,
        MerchantGoods pack,
        StorePurchasePlan plan,
        List<int> buyBackIndices,
        bool remotePurchase,
        bool hasNpc,
        DateTime now)
    {
        if (remotePurchase &&
            (character.ActiveRemoteShop is not { } session ||
             !session.IsValid(now) ||
             session.MerchantPackId != pack.Id))
        {
            character.ActiveRemoteShop = null;
            character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
            return;
        }

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
            SendPurchaseError(balanceError);
            return;
        }

        if (!HasInventorySpace(plan?.Items ?? [], buyBackItems.Keys))
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
                SendPurchaseError(GetInsufficientCurrencyError(planCost, (int)totalMoney));
                return;
            case StorePurchaseTransactionResult.UnexpectedFailure:
                Logger.Error(failure, $"Store purchase transaction failed for character {character.Id}");
                character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
                return;
        }

        if (remotePurchase)
            character.ActiveRemoteShop = character.ActiveRemoteShop?.Refresh(now);

        Connection.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.StoreBuy, tasks, []));
        foreach (var packet in deferredSyncPackets)
            Connection.SendPacket(packet);
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

    private bool HasInventorySpace(
        IReadOnlyList<StorePurchaseItem> purchaseItems,
        IEnumerable<Item> buyBackItems)
    {
        var bag = Connection.ActiveChar.Inventory.Bag;
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

    private static bool IsNear(Character character, BaseUnit target)
    {
        var distance = MathUtil.CalculateDistance(
            character.Transform.World.Position,
            target.Transform.World.Position);
        if (distance <= 3f)
            return true;
        character.SendErrorMessage(ErrorMessageType.TooFarAway);
        return false;
    }

    private void SendPurchaseError(StorePurchaseError error)
    {
        var message = error switch
        {
            StorePurchaseError.NotEnoughMoney => ErrorMessageType.NotEnoughMoney,
            StorePurchaseError.NotEnoughHonor => ErrorMessageType.NotEnoughHonorPoint,
            StorePurchaseError.NotEnoughVocationBadges => ErrorMessageType.NotEnoughLivingPoint,
            _ => ErrorMessageType.StoreInvalidItem
        };
        Connection.ActiveChar.SendErrorMessage(message);
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
