using AAEmu.Game.Models.Game.Items.Containers;

namespace AAEmu.Game.Models.Game.Items;

internal static class InventoryPayment
{
    internal static bool TryConsume(InventoryMutation mutation, ItemContainer container, uint templateId, int count)
    {
        if (container == null || templateId == 0 || count <= 0)
            return false;
        var remaining = count;
        foreach (var item in container.Items.Where(item => item.TemplateId == templateId).OrderBy(item => item.Slot).ToArray())
        {
            var take = Math.Min(remaining, item.Count - TradeReservation.GetReservedCount(item));
            if (take <= 0)
                continue;
            if (!mutation.TryConsume(container, item, take))
                return false;
            remaining -= take;
            if (remaining == 0)
                return true;
        }
        return false;
    }
}
