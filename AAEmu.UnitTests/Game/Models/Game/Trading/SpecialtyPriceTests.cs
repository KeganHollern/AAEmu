using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Trading;

public sealed class SpecialtyPriceTests
{
    [Test]
    public async Task BasePrice_RoundsRefundBeforeRouteProfit()
    {
        // 3 × 150% -> 5. Then 5 + (1 × 500 / 1000) -> 6.
        await Assert.That(SpecialtyPrice.BasePrice(1, 500, 3, 150)).IsEqualTo(6);
        var payout = SpecialtyPrice.Calculate(10000, 130, false, true);
        await Assert.That(payout.Seller).IsEqualTo(10920);
        await Assert.That(payout.Crafter).IsEqualTo(2730);
        await Assert.That(payout.Seller + payout.Crafter).IsEqualTo(payout.PriceWithInterest);
    }

    [Test]
    public async Task CustomLateFee_ChangesOnlyAtDeadline_AndRoundsUpOnce()
    {
        var due = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        await Assert.That(HouseTaxAmount.WithLateFee(101, due, due.AddTicks(-1), 10)).IsEqualTo(101);
        await Assert.That(HouseTaxAmount.WithLateFee(101, due, due, 10)).IsEqualTo(112);
        await Assert.That(HouseTaxAmount.WithLateFee(101, due, due.AddDays(6), 10)).IsEqualTo(112);
        await Assert.That(HouseTaxAmount.WithLateFee(101, due, due, 0)).IsEqualTo(101);
    }
}
