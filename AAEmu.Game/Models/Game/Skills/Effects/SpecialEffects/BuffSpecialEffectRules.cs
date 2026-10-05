using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

internal static class BuffSpecialEffectRules
{
    internal static IEnumerable<Buff> GetRemovableGoodBuffs(BaseUnit target, DateTime now)
    {
        var good = new List<Buff>();
        target.Buffs.GetAllBuffs(good, [], [], false);
        return good.Where(buff => buff.InUse && !buff.IsEnded() &&
            !buff.Passive && !buff.Template.System && buff.Template.Kind == BuffKind.Good &&
            (buff.Duration == 0 || RemainingDuration(buff, now) > 0))
            .OrderBy(buff => buff.Index).ToArray();
    }

    // Custom server rule approved for #321. The exact retail formula is not known.
    internal static int RollResourceAmount(int maximum, int cap, int minimumPercent, int maximumPercent)
    {
        var percent = Random.Shared.Next(minimumPercent, maximumPercent + 1);
        return (int)Math.Clamp((long)maximum * percent / 100, 0, Math.Max(0, cap));
    }

    internal static int RemainingDuration(Buff buff, DateTime now)
    {
        if (buff.Duration == 0)
            return 0;
        return (int)Math.Clamp((buff.StartTime.AddMilliseconds(buff.Duration) - now).TotalMilliseconds, 0, int.MaxValue);
    }
}
