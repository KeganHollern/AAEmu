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

        var buyBackItems = new Dictionary<Item, int>();
        long buyBackCost = 0;
        if (buyBackIndices.Count > 0)
        {
            if (remotePurchase || npc == null || pack.Currency != ShopCurrencyType.Money)
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

        foreach (var item in plan?.Items ?? [])
        {
            if (!character.Inventory.Bag.AcquireDefaultItem(
                    ItemTaskType.StoreBuy, item.ItemId, item.Count, item.Grade))
            {
                character.SendErrorMessage(ErrorMessageType.BagFull);
                return;
            }
        }

        var tasks = new List<ItemTask>();
        foreach (var (item, _) in buyBackItems)
        {
            if (!character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.StoreBuy, item))
            {
                character.SendErrorMessage(ErrorMessageType.BagFull);
                return;
            }
            tasks.Add(new ItemBuyback(item));
        }

        if (planCost.Honor > 0)
            character.ChangeGamePoints(GamePointKind.Honor, -planCost.Honor);
        if (planCost.VocationBadges > 0)
            character.ChangeGamePoints(GamePointKind.Vocation, -planCost.VocationBadges);
        if (totalMoney > 0)
            character.ChangeMoney(SlotType.Inventory, -(int)totalMoney);

        if (remotePurchase)
            character.ActiveRemoteShop = character.ActiveRemoteShop?.Refresh(now);

        Connection.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.StoreBuy, tasks, []));
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
}
