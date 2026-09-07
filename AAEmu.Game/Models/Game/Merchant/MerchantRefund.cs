using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Merchant;

public static class MerchantRefund
{
    /// <summary>Uses the same checked whole-stack price for selling and buying an item back.</summary>
    public static bool TryCalculate(Item item, GradeTemplate grade, out int refund)
    {
        refund = 0;
        if (item?.Template == null || grade == null || item.Count <= 0 || item.Count > item.Template.MaxCount ||
            item.Template.Refund < 0 || grade.RefundMultiplier < 0)
            return false;
        try
        {
            // Truncate the grade-adjusted unit price before multiplying by the stack count.
            // Widen before either multiplication; every wire money delta is a signed int.
            var unitRefund = checked((long)item.Template.Refund * grade.RefundMultiplier) / 100;
            var total = checked(unitRefund * item.Count);
            if (total > int.MaxValue)
                return false;
            refund = (int)total;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
