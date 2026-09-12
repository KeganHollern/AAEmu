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

    [Test]
    [Arguments((byte)0, 0, false)]
    [Arguments((byte)1, 0, true)]
    [Arguments((byte)1, 1, false)]
    [Arguments((byte)7, 6, true)]
    [Arguments((byte)7, 7, false)]
    [Arguments((byte)255, 7, false)]
    public async Task Negotiation_UsesRankPercentWithSevenPercentCap(byte rank, int roll, bool success)
    {
        await Assert.That(SpecialtyPrice.Negotiates(rank, roll)).IsEqualTo(success);
    }

    [Test]
    public async Task Negotiation_AddsFivePercentBeforeInterestAndMakerShare()
    {
        var payout = SpecialtyPrice.Calculate(10000, 130, false, true, true);
        await Assert.That(payout.NegotiationBonus).IsEqualTo(650);
        await Assert.That(payout.Price).IsEqualTo(13650);
        await Assert.That(payout.PriceWithInterest).IsEqualTo(14333);
        await Assert.That(payout.Seller).IsEqualTo(11466);
        await Assert.That(payout.Crafter).IsEqualTo(2867);
        await Assert.That(payout.Seller + payout.Crafter).IsEqualTo(payout.PriceWithInterest);
    }

    [Test]
    public async Task Negotiation_FloorsBonusToWholeCurrencyUnits()
    {
        await Assert.That(SpecialtyPrice.Calculate(19, 100, false, false, true).NegotiationBonus).IsEqualTo(0);
        await Assert.That(SpecialtyPrice.Calculate(199999, 100, true, false, true).NegotiationBonus).IsEqualTo(0);
        var items = SpecialtyPrice.Calculate(400000, 100, true, true, true);
        await Assert.That(items.NegotiationBonus).IsEqualTo(20000);
        await Assert.That(items.Seller + items.Crafter).IsEqualTo(44);
    }
}
