using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills;

internal static class AttackTiming
{
    internal static double GetWeaponInterval(Unit caster, double baseMilliseconds, bool ranged, bool twoHanded)
    {
        var attribute = ranged ? UnitAttribute.RangedSpeedMul : UnitAttribute.MeleeSpeedMul;
        var bonus = Math.Truncate(caster.CalculateWithBonuses(0d, attribute));
        if (twoHanded)
            bonus += Math.Truncate(caster.CalculateWithBonuses(0d, UnitAttribute.TwohandSpeedMul));

        // Native weapon timing uses a -999 denominator floor after the raw limits.
        // The 1 ms floor keeps the server's recurring task interval positive.
        var milliseconds = Math.Truncate(baseMilliseconds * 1000d / (1000d + Math.Max(-999d, bonus)));
        return Math.Max(1d, milliseconds);
    }

    internal static int ScaleAnimationTime(Unit caster, int milliseconds)
    {
        var bonus = Math.Truncate(caster.CalculateWithBonuses(0d, UnitAttribute.AttackAnimSpeedMul));
        var rate = Math.Max(0.1d, 1d + Math.Max(-999d, bonus) / 1000d);
        return (int)Math.Clamp(Math.Truncate(milliseconds / rate), 0d, int.MaxValue);
    }
}
