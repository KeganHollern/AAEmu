using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;

namespace AAEmu.Game.Models.Game.Merchant;

public enum StorePurchaseTransactionResult
{
    Success,
    InventoryFailed,
    CurrencyFailed,
    UnexpectedFailure
}

/// <summary>
/// Restores the character wallet and store-related containers when a purchase cannot commit.
/// Callers must hold <see cref="Character.StorePurchaseSyncRoot"/> while executing the transaction.
/// </summary>
public sealed class StorePurchaseTransaction
{
    private readonly Character _character;
    private readonly Action<ulong> _releaseItemId;
    private readonly ItemContainer _bagContainer;
    private readonly ItemContainer _buyBackContainer;
    private readonly ContainerSnapshot _bag;
    private readonly ContainerSnapshot _buyBack;
    private readonly long _money;
    private readonly int _honorPoint;
    private readonly int _vocationPoint;
    private bool _completed;

    public StorePurchaseTransaction(Character character, Action<ulong> releaseItemId)
        : this(
            character,
            character?.Inventory?.Bag,
            character?.BuyBackItems,
            releaseItemId)
    {
    }

    public StorePurchaseTransaction(
        Character character,
        ItemContainer bagContainer,
        ItemContainer buyBackContainer,
        Action<ulong> releaseItemId)
    {
        _character = character ?? throw new ArgumentNullException(nameof(character));
        _releaseItemId = releaseItemId ?? throw new ArgumentNullException(nameof(releaseItemId));
        _bagContainer = bagContainer ?? throw new ArgumentNullException(nameof(bagContainer));
        _buyBackContainer = buyBackContainer ?? throw new ArgumentNullException(nameof(buyBackContainer));
        _bag = new ContainerSnapshot(_bagContainer);
        _buyBack = new ContainerSnapshot(_buyBackContainer);
        _money = character.Money;
        _honorPoint = character.HonorPoint;
        _vocationPoint = character.VocationPoint;
    }

    public StorePurchaseTransactionResult Execute(
        Func<bool> mutateInventory,
        Func<bool> spendCurrency,
        out Exception failure)
    {
        ArgumentNullException.ThrowIfNull(mutateInventory);
        ArgumentNullException.ThrowIfNull(spendCurrency);
        failure = null;

        try
        {
            if (!mutateInventory())
            {
                Rollback();
                return StorePurchaseTransactionResult.InventoryFailed;
            }

            if (!spendCurrency())
            {
                Rollback();
                return StorePurchaseTransactionResult.CurrencyFailed;
            }

            _completed = true;
            return StorePurchaseTransactionResult.Success;
        }
        catch (Exception exception)
        {
            failure = exception;
            Rollback();
            return StorePurchaseTransactionResult.UnexpectedFailure;
        }
    }

    private void Rollback()
    {
        if (_completed)
            return;

        _character.Money = _money;
        _character.HonorPoint = _honorPoint;
        _character.VocationPoint = _vocationPoint;

        var originalItems = _bag.Items
            .Concat(_buyBack.Items)
            .Select(state => state.Item)
            .ToHashSet();
        var createdItems = _bagContainer.Items
            .Concat(_buyBackContainer.Items)
            .Where(item => !originalItems.Contains(item))
            .Distinct()
            .ToList();

        _bag.Restore();
        _buyBack.Restore();

        foreach (var item in createdItems)
        {
            item._holdingContainer = null;
            _releaseItemId(item.Id);
        }
    }

    private sealed class ContainerSnapshot
    {
        private readonly ItemContainer _container;
        private readonly bool _isDirty;

        public ContainerSnapshot(ItemContainer container)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _isDirty = container.IsDirty;
            Items = container.Items.Select(item => new ItemSnapshot(item)).ToList();
        }

        public IReadOnlyList<ItemSnapshot> Items { get; }

        public void Restore()
        {
            foreach (var item in Items)
                item.Restore();

            _container.Items = Items.Select(state => state.Item).ToList();
            _container.IsDirty = _isDirty;
            _container.UpdateFreeSlotCount();
        }
    }

    private sealed class ItemSnapshot
    {
        private readonly ItemContainer _holdingContainer;
        private readonly bool _isDirty;
        private readonly ulong _ownerId;
        private readonly SlotType _slotType;
        private readonly int _slot;
        private readonly int _count;
        private readonly ItemFlag _itemFlags;

        public ItemSnapshot(Item item)
        {
            Item = item;
            _holdingContainer = item._holdingContainer;
            _isDirty = item.IsDirty;
            _ownerId = item.OwnerId;
            _slotType = item.SlotType;
            _slot = item.Slot;
            _count = item.Count;
            _itemFlags = item.ItemFlags;
        }

        public Item Item { get; }

        public void Restore()
        {
            Item.OwnerId = _ownerId;
            Item.SlotType = _slotType;
            Item.Slot = _slot;
            Item.Count = _count;
            Item.ItemFlags = _itemFlags;
            Item._holdingContainer = _holdingContainer;
            Item.IsDirty = _isDirty;
        }
    }
}
