using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Trading;

namespace AAEmu.UnitTests.Game.Models.Game.Trading;

public sealed class SpecialtyDemandTests
{
    private static readonly DateTime Start = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Restart_PreservesPendingSalesAndPartialRegenerationPeriod()
    {
        var config = new SpecialtyConfig();
        var demand = SpecialtyDemand.Create(42, 7, Start, config);
        demand = demand.RecordSale(Start.AddSeconds(20), config);
        var saved = demand with { };
        var restored = saved.Advance(Start.AddMinutes(30), config);

        await Assert.That(restored.Ratio).IsEqualTo(129m);
        await Assert.That(restored.PendingSales).IsEqualTo(0);
        await Assert.That(restored.RegenerateAt).IsEqualTo(Start.AddHours(1));
        await Assert.That(restored.Advance(Start.AddHours(1).AddTicks(-1), config).Ratio).IsEqualTo(129m);
        await Assert.That(restored.Advance(Start.AddHours(1), config).Ratio).IsEqualTo(130m);
    }

    [Test]
    public async Task OfflineAdvance_MatchesLiveTicksAcrossConsumptionAndRegeneration()
    {
        var config = new SpecialtyConfig { RatioDecreaseTickMinutes = 90, RatioRegenTickMinutes = 60 };
        var saved = SpecialtyDemand.Create(42, 7, Start, config) with { Ratio = 80m, PendingSales = 40 };
        var live = saved;
        for (var minute = 1; minute <= 240; minute++)
            live = live.Advance(Start.AddMinutes(minute), config);

        var restored = saved.Advance(Start.AddHours(4), config);
        await Assert.That(restored).IsEqualTo(live);
        await Assert.That(restored.Ratio).IsEqualTo(85m);
    }

    [Test]
    public async Task SimultaneousTicks_ConsumeBeforeRegeneration_AndKeepRatioBounds()
    {
        var config = new SpecialtyConfig { RatioDecreaseTickMinutes = 60 };
        var demand = SpecialtyDemand.Create(42, 7, Start, config) with { PendingSales = 1000 };

        var tick = demand.Advance(Start.AddHours(1), config);
        await Assert.That(tick.Ratio).IsEqualTo(75m);
        await Assert.That(tick.Advance(Start.AddDays(365), config).Ratio).IsEqualTo(130m);
    }

    [Test]
    public async Task RecordSale_AfterConsumeDeadline_StartsTheNextBatch()
    {
        var config = new SpecialtyConfig();
        var demand = SpecialtyDemand.Create(42, 7, Start, config)
            .RecordSale(Start, config)
            .RecordSale(Start.AddMinutes(1), config);

        await Assert.That(demand.Ratio).IsEqualTo(129m);
        await Assert.That(demand.PendingSales).IsEqualTo(1);
        await Assert.That(demand.ConsumeAt).IsEqualTo(Start.AddMinutes(2));
        await Assert.That(demand.Advance(Start.AddMinutes(2), config).Ratio).IsEqualTo(128m);
    }

    [Test]
    public async Task EarlierClock_DoesNotRegenerateOrConsumeAgain()
    {
        var config = new SpecialtyConfig();
        var demand = SpecialtyDemand.Create(42, 7, Start, config).RecordSale(Start, config);

        await Assert.That(demand.Advance(Start.AddMinutes(-1), config)).IsEqualTo(demand);
    }
}
