using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Models.Game.Merchant;

public readonly record struct MerchantSaleRequest(SlotType SlotType, byte Slot, ulong ItemId);

public enum MerchantSaleResult
{
    Success,
    InvalidMerchant,
    TooFarAway,
    InvalidItem,
    Failed
}

public static class MerchantSaleExecutor
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Sells whole authoritative stacks. A failed batch restores every item and the wallet
    /// before returning, so the packet handler can safely report the error.
    /// </summary>
    public static MerchantSaleResult Execute(Character character, uint npcObjId,
        IReadOnlyList<MerchantSaleRequest> requests)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var validation = Validate(character, npcObjId, requests, out var items, out var refund);
            if (validation != MerchantSaleResult.Success)
                return validation;

            using var mutation = new InventoryMutation(ItemTaskType.StoreSell);
            try
            {
                foreach (var item in items)
                    if (!mutation.TryMove(item, character.BuyBackItems))
                        return MerchantSaleResult.Failed;
                if (!mutation.TryChangeMoney(character, refund))
                    return MerchantSaleResult.Failed;
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Merchant sale preparation failed for character {0}", character.Id);
                return MerchantSaleResult.Failed;
            }

            // Buyback is not persisted. Queue the old rows only after every move and the
            // wallet credit succeeded, while the periodic save is excluded by this lock.
            // Buying an item back makes it dirty in a persisted container again.
            foreach (var item in items)
                ItemManager.Instance.MarkItemForDbDeletion(item.Id);
            mutation.Complete();
            return MerchantSaleResult.Success;
        }
    }

    private static MerchantSaleResult Validate(Character character, uint npcObjId,
        IReadOnlyList<MerchantSaleRequest> requests, out List<Item> items, out int refund)
    {
        items = [];
        refund = 0;
        var world = character?.ParentWorld;
        var npc = npcObjId == 0 ? null : world?.GetNpc(npcObjId);
        if (npc?.Template?.Merchant != true || npc.ObjId != npcObjId || npc.Despawned ||
            !ReferenceEquals(npc.ParentWorld, world) || character.Transform == null || npc.Transform == null ||
            character.Transform.InstanceId != npc.Transform.InstanceId ||
            character.Transform.WorldId != npc.Transform.WorldId)
            return MerchantSaleResult.InvalidMerchant;

        // Match CSBuyItemsPacket's existing three metre horizontal interaction range.
        if (!(MathUtil.CalculateDistance(character.Transform.World.Position, npc.Transform.World.Position) <= 3f))
            return MerchantSaleResult.TooFarAway;

        var buyback = character.BuyBackItems;
        if (requests == null || requests.Count is < 1 or > byte.MaxValue || character.Inventory == null ||
            buyback == null || buyback.OwnerId != character.Id || buyback.ContainerType != SlotType.None)
            return MerchantSaleResult.InvalidItem;

        var slots = new HashSet<(SlotType, byte)>();
        var ids = new HashSet<ulong>();
        long total = 0;
        foreach (var request in requests)
        {
            if (!slots.Add((request.SlotType, request.Slot)) || request.ItemId == 0 || !ids.Add(request.ItemId))
                return MerchantSaleResult.InvalidItem;
            ItemContainer container = request.SlotType switch
            {
                SlotType.Inventory => character.Inventory.Bag,
                SlotType.Equipment => character.Inventory.Equipment,
                _ => null
            };
            if (container == null || container.OwnerId != character.Id || container.ContainerType != request.SlotType ||
                request.Slot >= container.ContainerSize)
                return MerchantSaleResult.InvalidItem;
            var item = container.GetItemBySlot(request.Slot);
            if (item == null || item.Id != request.ItemId || item.OwnerId != character.Id ||
                item.SlotType != request.SlotType || item.Slot != request.Slot ||
                !ReferenceEquals(item._holdingContainer, container) ||
                !ReferenceEquals(ItemManager.Instance.GetItemByItemId(item.Id), item) ||
                container.Items.Count(candidate => candidate.Id == item.Id || candidate.Slot == request.Slot) != 1 ||
                item.Template?.Sellable != true || item.TemplateId != item.Template.Id)
                return MerchantSaleResult.InvalidItem;
            var grade = ItemManager.Instance.GetGradeTemplate(item.Grade);
            if (!MerchantRefund.TryCalculate(item, grade, out var itemRefund))
                return MerchantSaleResult.InvalidItem;
            total = checked(total + itemRefund);
            if (total > int.MaxValue)
                return MerchantSaleResult.InvalidItem;
            items.Add(item);
        }
        refund = (int)total;
        return MerchantSaleResult.Success;
    }
}
