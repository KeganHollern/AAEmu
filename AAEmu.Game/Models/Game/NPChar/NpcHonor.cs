using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.NPChar;

public partial class Npc
{
    internal void GrantKillHonor(Character recipient)
    {
        // Honor uses the tagged kill recipients, not the broader quest tag-share list.
        // A retained tag must not award a player who left this instance or kill range.
        if (Template.HonorPoint <= 0 || recipient is not { IsOnline: true } ||
            ParentWorld == null || !ReferenceEquals(recipient.ParentWorld, ParentWorld) ||
            recipient.Transform.WorldId != Transform.WorldId ||
            recipient.Transform.InstanceId != Transform.InstanceId ||
            recipient.GetDistanceTo(this, true) > LootingContainer.MaxLootingRange)
            return;

        var rate = AppConfiguration.Instance.World.HonorRate;
        if (!double.IsFinite(rate) || rate <= 0)
            return;

        var flatBonus = recipient.CalculateWithBonuses(0d, UnitAttribute.HonorPointGainNpcKill);
        var multiplier = recipient.CalculateWithBonuses(100d, UnitAttribute.HonorPointGainNpcKillMul) / 100d;
        var reward = Math.Round(Math.Max(0d, Template.HonorPoint + flatBonus) * Math.Max(0d, multiplier) * rate);
        if (double.IsNaN(reward) || reward <= 0)
            return;

        lock (recipient.StorePurchaseSyncRoot)
        {
            var available = Math.Max(0L, (long)int.MaxValue - recipient.HonorPoint);
            var amount = (int)Math.Min(Math.Min(reward, int.MaxValue), available);
            if (amount > 0)
                recipient.ChangeGamePoints(GamePointKind.Honor, amount);
        }
    }
}
