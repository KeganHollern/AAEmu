using AAEmu.Game.Models.Game.Items;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterMoneyTests
{
    [Test]
    [Arguments(SlotType.Inventory)]
    [Arguments(SlotType.Bank)]
    public async Task SubtractMoney_InsufficientFunds_RejectsWithoutChangingEitherWallet(SlotType wallet)
    {
        var character = new CharacterMock { Money = 99, Money2 = 99 };

        var result = character.SubtractMoney(wallet, 100);

        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(99L);
        await Assert.That(character.Money2).IsEqualTo(99L);
    }

    [Test]
    [Arguments(SlotType.Inventory)]
    [Arguments(SlotType.Bank)]
    public async Task SubtractMoney_ExactBalance_DebitsOnlyTheRequestedWallet(SlotType wallet)
    {
        var character = new CharacterMock { Money = 100, Money2 = 100 };

        var result = character.SubtractMoney(wallet, 100);

        await Assert.That(result).IsTrue();
        await Assert.That(character.Money).IsEqualTo(wallet == SlotType.Inventory ? 0L : 100L);
        await Assert.That(character.Money2).IsEqualTo(wallet == SlotType.Bank ? 0L : 100L);
    }

    [Test]
    [Arguments(SlotType.Inventory)]
    [Arguments(SlotType.Bank)]
    public async Task SignedChangeMoney_InsufficientDebit_UsesTheSameFundsCheck(SlotType wallet)
    {
        var character = new CharacterMock { Money = 99, Money2 = 99 };

        var result = character.ChangeMoney(wallet, -100);

        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(99L);
        await Assert.That(character.Money2).IsEqualTo(99L);
    }

    [Test]
    [Arguments(SlotType.Inventory)]
    [Arguments(SlotType.Bank)]
    public async Task AddMoney_Overflow_RejectsWithoutWrappingEitherWallet(SlotType wallet)
    {
        var character = new CharacterMock { Money = long.MaxValue, Money2 = long.MaxValue };

        var result = character.AddMoney(wallet, 1);

        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(long.MaxValue);
        await Assert.That(character.Money2).IsEqualTo(long.MaxValue);
    }

    [Test]
    [Arguments(SlotType.Inventory, SlotType.Bank)]
    [Arguments(SlotType.Bank, SlotType.Inventory)]
    public async Task Transfer_DestinationOverflow_PreservesTheSourceBalance(SlotType source, SlotType destination)
    {
        var character = new CharacterMock { Money = long.MaxValue, Money2 = long.MaxValue };

        var result = character.ChangeMoney(source, destination, 1);

        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(long.MaxValue);
        await Assert.That(character.Money2).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task Transfer_NegativeRequest_RejectsWithoutReversingTheTransfer()
    {
        var character = new CharacterMock { Money = 100, Money2 = 200 };

        var result = character.ChangeMoney(SlotType.Inventory, SlotType.Bank, -50);

        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(100L);
        await Assert.That(character.Money2).IsEqualTo(200L);
    }

    [Test]
    public async Task Transfer_ExactSourceBalance_ConservesBothWallets()
    {
        var character = new CharacterMock { Money = 100, Money2 = 200 };

        var result = character.ChangeMoney(SlotType.Inventory, SlotType.Bank, 100);

        await Assert.That(result).IsTrue();
        await Assert.That(character.Money).IsEqualTo(0L);
        await Assert.That(character.Money2).IsEqualTo(300L);
    }

    [Test]
    public async Task SubtractMoney_CompetingDebits_OnlyOneSpendsTheAvailableFunds()
    {
        var character = new CharacterMock { Money = 100 };
        var successes = 0;

        Parallel.For(0, 20, _ =>
        {
            if (character.SubtractMoney(SlotType.Inventory, 100))
                Interlocked.Increment(ref successes);
        });

        await Assert.That(successes).IsEqualTo(1);
        await Assert.That(character.Money).IsEqualTo(0L);
    }

    [Test]
    public async Task AddMoney_CompetingCredits_OnlyOneFitsTheRemainingCapacity()
    {
        var character = new CharacterMock { Money = long.MaxValue - 1 };
        var successes = 0;

        Parallel.For(0, 20, _ =>
        {
            if (character.AddMoney(SlotType.Inventory, 1))
                Interlocked.Increment(ref successes);
        });

        await Assert.That(successes).IsEqualTo(1);
        await Assert.That(character.Money).IsEqualTo(long.MaxValue);
    }
}
