namespace AAEmu.Game.Models.Game.Housing;

public static class HouseTaxAmount
{
    public static int WithLateFee(int weeklyTax, DateTime dueAt, DateTime utcNow, uint lateFeePercent)
    {
        if (weeklyTax < 0 || lateFeePercent > 100)
            throw new ArgumentOutOfRangeException(nameof(lateFeePercent), "The custom late fee must be between 0 and 100 percent.");
        if (utcNow < dueAt || lateFeePercent == 0)
            return weeklyTax;
        return checked(weeklyTax + (int)decimal.Ceiling(weeklyTax * (decimal)lateFeePercent / 100m));
    }
}
