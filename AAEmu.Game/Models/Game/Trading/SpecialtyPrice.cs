namespace AAEmu.Game.Models.Game.Trading;

public static class SpecialtyPrice
{
    // r208022 rounds the grade-adjusted refund, then the complete base price.
    public static int BasePrice(uint profit, uint ratio, int refund, int gradeRefundPercent)
    {
        var adjustedRefund = Round(refund * (decimal)gradeRefundPercent / 100m);
        return Round(profit * (decimal)ratio / 1000m + adjustedRefund);
    }

    public static int Round(decimal amount) => checked((int)decimal.Floor(amount + 0.5m));

    public static SpecialtyPayout Calculate(int basePrice, int demandPercent, bool itemReward, bool shareWithCrafter)
    {
        var price = Round(basePrice * (decimal)demandPercent / 100m);
        const int interestPercent = 5;
        var withInterest = Round(price * (100m + interestPercent) / 100m);
        var total = itemReward ? Round(withInterest / 10000m) : withInterest;
        var seller = shareWithCrafter ? Round(total * 0.8m) : total;
        return new(price, withInterest, seller, total - seller, interestPercent);
    }
}

public sealed record SpecialtyPayout(int Price, int PriceWithInterest, int Seller, int Crafter, int InterestPercent);
