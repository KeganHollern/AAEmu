using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Items;

internal static class ItemPickupPolicy
{
    // Call under PersistenceSyncRoot, before changing a stack or charging its source.
    // Mail and auction attachments are not held inventory until the recipient claims them.
    internal static bool CanAcquire(ItemContainer destination, ItemTemplate template, int count,
        Item movingItem = null, Item leavingItem = null)
    {
        if (template == null || count <= 0)
            return false;
        if (template.PickupLimit <= 0)
            return true;
        var inventory = destination?.Owner?.Inventory;
        if (inventory == null ||
            !IsHeldContainer(destination, inventory))
            return true;

        var containers = new[] { inventory.Bag, inventory.Warehouse, inventory.Equipment };
        // An internal move or split does not acquire another item, even for legacy over-limit stacks.
        if (movingItem != null && containers.Any(container => container?.Items.Contains(movingItem) == true))
            return true;

        var held = containers.Where(container => container != null).Distinct()
            .SelectMany(container => container.Items)
            .Where(item => item.TemplateId == template.Id)
            .Sum(item => (long)item.Count);
        if (leavingItem?.TemplateId == template.Id &&
            containers.Any(container => container?.Items.Contains(leavingItem) == true))
            held -= leavingItem.Count;
        if (held + count <= template.PickupLimit)
            return true;

        destination.Owner.SendErrorMessage(ErrorMessageType.ItemPickupLimit);
        return false;
    }

    private static bool IsHeldContainer(ItemContainer container, Char.Inventory inventory) =>
        ReferenceEquals(container, inventory.Bag) || ReferenceEquals(container, inventory.Warehouse) ||
        ReferenceEquals(container, inventory.Equipment);
}
