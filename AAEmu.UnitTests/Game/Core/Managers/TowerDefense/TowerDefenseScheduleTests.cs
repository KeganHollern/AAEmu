using AAEmu.Game.Core.Managers.TowerDefense;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.UnitTests.Game.Core.Managers.TowerDefense;

public class TowerDefenseScheduleTests
{
    [Test]
    public async Task TryGetCrossedDay_ForwardCrossing_StartsOnceOnCurrentDay()
    {
        var trigger = Trigger(12f);
        var tick = Tick(11.9f, 12.1f, 100, 100);

        var crossed = TowerDefenseSchedule.TryGetCrossedDay(tick, trigger, out var day);

        await Assert.That(crossed).IsTrue();
        await Assert.That(day).IsEqualTo(100L);
    }

    [Test]
    public async Task TryGetCrossedDay_MidnightWrap_AssignsPreMidnightTargetToPreviousDay()
    {
        var trigger = Trigger(23.9f);
        var tick = Tick(23.8f, 0.2f, 100, 101);

        var crossed = TowerDefenseSchedule.TryGetCrossedDay(tick, trigger, out var day);

        await Assert.That(crossed).IsTrue();
        await Assert.That(day).IsEqualTo(100L);
    }

    [Test]
    public async Task TryGetCrossedDay_BackwardSourceWindow_IsSuppressed()
    {
        var trigger = Trigger(1.5f);
        var previous = new WorldClockSnapshot(1f, 100, 2401, DateTimeOffset.UtcNow);
        var current = new WorldClockSnapshot(2f, 100, 2400, DateTimeOffset.UtcNow);

        var crossed = TowerDefenseSchedule.TryGetCrossedDay(
            new WorldClockTick(previous, current, false), trigger, out _);

        await Assert.That(crossed).IsFalse();
    }

    [Test]
    public async Task TryGetCrossedDay_BackwardManualDisplayChange_IsSuppressed()
    {
        var trigger = Trigger(23f);
        var previous = new WorldClockSnapshot(13f, 100, 2413, DateTimeOffset.UtcNow);
        var current = new WorldClockSnapshot(11f, 100, 2413, DateTimeOffset.UtcNow);

        var crossed = TowerDefenseSchedule.TryGetCrossedDay(
            new WorldClockTick(previous, current, true), trigger, out _);

        await Assert.That(crossed).IsFalse();
    }

    [Test]
    public async Task TryGetCrossedDay_DayIntervalAndPhase_FiltersOtherDays()
    {
        var trigger = Trigger(12f);
        trigger.DayInterval = 3;
        trigger.DayPhase = 1;

        var rejected = TowerDefenseSchedule.TryGetCrossedDay(Tick(11f, 13f, 101, 101), trigger, out _);
        var accepted = TowerDefenseSchedule.TryGetCrossedDay(Tick(11f, 13f, 103, 103), trigger, out _);

        await Assert.That(rejected).IsFalse();
        await Assert.That(accepted).IsTrue();
    }

    [Test]
    public async Task IsInsideCatchUpWindow_UsesConfiguredRealSecondGrace()
    {
        var trigger = Trigger(12f);
        trigger.CatchUpGraceSeconds = 30;
        var inside = new WorldClockSnapshot(12.04f, 1, 36.04, DateTimeOffset.UtcNow);
        var outside = new WorldClockSnapshot(12.06f, 1, 36.06, DateTimeOffset.UtcNow);

        await Assert.That(TowerDefenseSchedule.IsInsideCatchUpWindow(inside, trigger, 1f / 600f)).IsTrue();
        await Assert.That(TowerDefenseSchedule.IsInsideCatchUpWindow(outside, trigger, 1f / 600f)).IsFalse();
    }

    private static TowerDefenseTriggerManifest Trigger(float hour) => new()
    {
        Type = "TimeOfDay",
        Hour = hour,
        DayInterval = 1,
        DayPhase = 0
    };

    private static WorldClockTick Tick(float previousHours, float currentHours, long previousDay, long currentDay)
    {
        var previous = new WorldClockSnapshot(previousHours, previousDay,
            previousDay * 24d + previousHours, DateTimeOffset.UtcNow);
        var current = new WorldClockSnapshot(currentHours, currentDay,
            currentDay * 24d + currentHours, DateTimeOffset.UtcNow);
        return new WorldClockTick(previous, current, false);
    }
}
