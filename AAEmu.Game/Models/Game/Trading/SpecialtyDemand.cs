namespace AAEmu.Game.Models.Game.Trading;

/// <summary>
/// Demand for one pack at one destination. The saved deadlines preserve the
/// consumption and regeneration phase through restarts and partial timer ticks.
/// </summary>
public sealed record SpecialtyDemand(
    uint ItemId,
    uint ZoneGroupId,
    decimal Ratio,
    int PendingSales,
    DateTime ConsumeAt,
    DateTime RegenerateAt)
{
    public static SpecialtyDemand Create(uint itemId, uint zoneGroupId, DateTime utcNow, SpecialtyConfig config)
    {
        Validate(config);
        return new(itemId, zoneGroupId, config.MaxSpecialtyRatio, 0,
            utcNow.AddMinutes(config.RatioDecreaseTickMinutes),
            utcNow.AddMinutes(config.RatioRegenTickMinutes));
    }

    public SpecialtyDemand Advance(DateTime utcNow, SpecialtyConfig config)
    {
        Validate(config);
        var consumePeriod = TimeSpan.FromMinutes(config.RatioDecreaseTickMinutes);
        var regenPeriod = TimeSpan.FromMinutes(config.RatioRegenTickMinutes);
        var ratio = Math.Clamp(Ratio, config.MinSpecialtyRatio, config.MaxSpecialtyRatio);
        var pending = PendingSales;
        var regenerateAt = RegenerateAt;

        // Regeneration before the pending consumption must occur first. At the
        // same timestamp, consume first, as the live timer does.
        if (pending > 0 && ConsumeAt <= utcNow)
        {
            var earlierTicks = CountTicks(regenerateAt, ConsumeAt.AddTicks(-1), regenPeriod);
            ratio = Regenerate(ratio, earlierTicks, config);
            regenerateAt = regenerateAt.AddTicks(earlierTicks * regenPeriod.Ticks);
            ratio = Math.Max(config.MinSpecialtyRatio,
                ratio - decimal.Ceiling(pending * (decimal)config.RatioDecreasePerPack));
            pending = 0;
        }

        var regenTicks = CountTicks(regenerateAt, utcNow, regenPeriod);
        ratio = Regenerate(ratio, regenTicks, config);
        regenerateAt = regenerateAt.AddTicks(regenTicks * regenPeriod.Ticks);
        var consumeTicks = CountTicks(ConsumeAt, utcNow, consumePeriod);
        return this with
        {
            Ratio = ratio,
            PendingSales = pending,
            ConsumeAt = ConsumeAt.AddTicks(consumeTicks * consumePeriod.Ticks),
            RegenerateAt = regenerateAt
        };
    }

    public SpecialtyDemand RecordSale(DateTime utcNow, SpecialtyConfig config)
    {
        var current = Advance(utcNow, config);
        return current with { PendingSales = checked(current.PendingSales + 1) };
    }

    private static long CountTicks(DateTime first, DateTime now, TimeSpan period) =>
        now < first ? 0 : (now.Ticks - first.Ticks) / period.Ticks + 1;

    private static decimal Regenerate(decimal ratio, long ticks, SpecialtyConfig config) =>
        Math.Min(config.MaxSpecialtyRatio, ratio + ticks * (decimal)config.RatioIncreasePerTick);

    internal static void Validate(SpecialtyConfig config)
    {
        if (config.MinSpecialtyRatio < 0 || config.MaxSpecialtyRatio < config.MinSpecialtyRatio ||
            !double.IsFinite(config.RatioDecreasePerPack) || config.RatioDecreasePerPack < 0 ||
            !double.IsFinite(config.RatioIncreasePerTick) || config.RatioIncreasePerTick < 0 ||
            !double.IsFinite(config.RatioDecreaseTickMinutes) || config.RatioDecreaseTickMinutes < 0.001 ||
            !double.IsFinite(config.RatioRegenTickMinutes) || config.RatioRegenTickMinutes < 0.001)
            throw new InvalidOperationException("Specialty demand settings need finite rates and positive timer periods.");
    }
}
