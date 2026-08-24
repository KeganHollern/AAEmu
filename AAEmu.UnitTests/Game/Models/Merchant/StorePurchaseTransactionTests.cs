using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Merchant;

public class StorePurchaseTransactionTests
{
    [Test]
    public async Task Execute_AllStepsSucceed_CommitsInventoryAndCurrency()
    {
        var character = new CharacterMock { HonorPoint = 2_000 };
        var bag = CreateContainer(character, SlotType.Inventory, 10);
        var buyBack = CreateContainer(character, SlotType.None, -1);
        var releasedIds = new List<ulong>();
        var purchasedItem = CreateItem(30, 400, 1, bag, 0);
        var transaction = new StorePurchaseTransaction(
            character,
            bag,
            buyBack,
            releasedIds.Add);

        var result = transaction.Execute(
            () =>
            {
                bag.Items.Add(purchasedItem);
                return true;
            },
            () =>
            {
                character.HonorPoint -= 500;
                return true;
            },
            out var failure);

        await Assert.That(result).IsEqualTo(StorePurchaseTransactionResult.Success);
        await Assert.That(failure).IsNull();
        await Assert.That(character.HonorPoint).IsEqualTo(1_500);
        await Assert.That(bag.Items).HasSingleItem();
        await Assert.That(bag.Items[0]).IsSameReferenceAs(purchasedItem);
        await Assert.That(releasedIds).IsEmpty();
    }

    [Test]
    public async Task Execute_SecondInventoryGrantFails_RestoresFirstGrantAndWallet()
    {
        var character = new CharacterMock
        {
            Money = 5_000,
            HonorPoint = 2_000,
            VocationPoint = 3_000
        };
        var bag = CreateContainer(character, SlotType.Inventory, 10);
        var buyBack = CreateContainer(character, SlotType.None, -1);
        var originalItem = CreateItem(1, 100, 2, bag, 0);
        bag.Items.Add(originalItem);
        bag.UpdateFreeSlotCount();
        var releasedIds = new List<ulong>();
        var currencyCalled = false;
        var transaction = new StorePurchaseTransaction(
            character,
            bag,
            buyBack,
            releasedIds.Add);

        var result = transaction.Execute(
            () =>
            {
                originalItem.Count += 3;
                var firstGrant = CreateItem(2, 101, 1, bag, 1);
                bag.Items.Add(firstGrant);
                bag.UpdateFreeSlotCount();
                return false; // The second requested grant failed.
            },
            () =>
            {
                currencyCalled = true;
                character.HonorPoint -= 500;
                return true;
            },
            out var failure);

        await Assert.That(result).IsEqualTo(StorePurchaseTransactionResult.InventoryFailed);
        await Assert.That(failure).IsNull();
        await Assert.That(currencyCalled).IsFalse();
        await Assert.That(character.Money).IsEqualTo(5_000);
        await Assert.That(character.HonorPoint).IsEqualTo(2_000);
        await Assert.That(character.VocationPoint).IsEqualTo(3_000);
        await Assert.That(originalItem.Count).IsEqualTo(2);
        await Assert.That(bag.Items).HasSingleItem();
        await Assert.That(bag.Items[0]).IsSameReferenceAs(originalItem);
        await Assert.That(releasedIds).IsEquivalentTo([2UL]);
    }

    [Test]
    public async Task Execute_CurrencyCommitFails_RestoresInventoryBuybackAndBalances()
    {
        var character = new CharacterMock
        {
            Money = 5_000,
            HonorPoint = 2_000,
            VocationPoint = 3_000
        };
        var bag = CreateContainer(character, SlotType.Inventory, 10);
        var buyBack = CreateContainer(character, SlotType.None, -1);
        var buyBackItem = CreateItem(10, 200, 1, buyBack, 4);
        buyBack.Items.Add(buyBackItem);
        buyBack.UpdateFreeSlotCount();
        var releasedIds = new List<ulong>();
        var transaction = new StorePurchaseTransaction(
            character,
            bag,
            buyBack,
            releasedIds.Add);

        var result = transaction.Execute(
            () =>
            {
                buyBack.Items.Remove(buyBackItem);
                buyBackItem._holdingContainer = bag;
                buyBackItem.SlotType = SlotType.Inventory;
                buyBackItem.Slot = 0;
                bag.Items.Add(buyBackItem);
                return true;
            },
            () =>
            {
                character.Money -= 1_000;
                return false;
            },
            out var failure);

        await Assert.That(result).IsEqualTo(StorePurchaseTransactionResult.CurrencyFailed);
        await Assert.That(failure).IsNull();
        await Assert.That(character.Money).IsEqualTo(5_000);
        await Assert.That(bag.Items).IsEmpty();
        await Assert.That(buyBack.Items).HasSingleItem();
        await Assert.That(buyBack.Items[0]).IsSameReferenceAs(buyBackItem);
        await Assert.That(buyBackItem._holdingContainer).IsSameReferenceAs(buyBack);
        await Assert.That(buyBackItem.SlotType).IsEqualTo(SlotType.None);
        await Assert.That(buyBackItem.Slot).IsEqualTo(4);
        await Assert.That(releasedIds).IsEmpty();
    }

    [Test]
    public async Task Execute_InventoryMutationThrows_RestoresStateAndReportsFailure()
    {
        var character = new CharacterMock { VocationPoint = 1_000 };
        var bag = CreateContainer(character, SlotType.Inventory, 10);
        var buyBack = CreateContainer(character, SlotType.None, -1);
        var originalItem = CreateItem(20, 300, 1, bag, 0);
        bag.Items.Add(originalItem);
        var transaction = new StorePurchaseTransaction(character, bag, buyBack, _ => { });

        var result = transaction.Execute(
            () =>
            {
                originalItem.Count = 99;
                throw new InvalidOperationException("forced failure");
            },
            () =>
            {
                character.VocationPoint -= 500;
                return true;
            },
            out var failure);

        await Assert.That(result).IsEqualTo(StorePurchaseTransactionResult.UnexpectedFailure);
        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(originalItem.Count).IsEqualTo(1);
        await Assert.That(character.VocationPoint).IsEqualTo(1_000);
    }

    private static ItemContainer CreateContainer(
        CharacterMock owner,
        SlotType slotType,
        int size)
    {
        var container = new ItemContainer(owner.Id, slotType, false, owner)
        {
            Owner = owner,
            ContainerSize = size
        };
        return container;
    }

    private static ItemMock CreateItem(
        uint id,
        uint templateId,
        int count,
        ItemContainer container,
        int slot)
    {
        var item = new ItemMock(
            id,
            new ItemTemplate
            {
                Id = templateId,
                MaxCount = 100,
                BindType = ItemBindType.Normal
            },
            count)
        {
            OwnerId = container.OwnerId,
            SlotType = container.ContainerType,
            Slot = slot,
            _holdingContainer = container,
            IsDirty = false
        };
        return item;
    }
}
