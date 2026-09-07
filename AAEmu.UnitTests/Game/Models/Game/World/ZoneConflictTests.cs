using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.World.Zones;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public sealed class ZoneConflictTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(true, false, ZoneConflictType.Tension)]
    [Arguments(false, false, ZoneConflictType.Conflict)]
    [Arguments(true, true, ZoneConflictType.Tension)]
    [Arguments(false, true, ZoneConflictType.Tension)]
    public async Task Restore_NoSavedState_UsesAuthoredCycle(bool killDriven, bool closed, ZoneConflictType expected)
    {
        var context = CreateContext(killDriven);
        context.Conflict.Closed = closed;

        context.Conflict.Restore(null);

        var snapshot = context.Conflict.GetSnapshot();
        await Assert.That(snapshot.State).IsEqualTo(expected);
        await Assert.That(snapshot.KillCount).IsEqualTo(0u);
        await Assert.That(snapshot.NextStateTime).IsEqualTo(expected == ZoneConflictType.Conflict
            ? Start.UtcDateTime.AddMinutes(5)
            : DateTime.MinValue);
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(ZoneConflictType.Tension, 70, ZoneConflictType.Danger)]
    [Arguments(ZoneConflictType.Danger, 100, ZoneConflictType.Dispute)]
    [Arguments(ZoneConflictType.Dispute, 140, ZoneConflictType.Unrest)]
    [Arguments(ZoneConflictType.Unrest, 190, ZoneConflictType.Crisis)]
    [Arguments(ZoneConflictType.Crisis, 250, ZoneConflictType.Conflict)]
    public async Task AddZoneKill_CumulativeBoundary_PreservesStrictGreaterThan(
        ZoneConflictType initial, uint threshold, ZoneConflictType next)
    {
        var context = CreateContext();
        context.Conflict.Restore(new ZoneConflictSnapshot(initial, threshold - 1, DateTime.MinValue));

        await Assert.That(context.Conflict.CurrentZoneState).IsEqualTo(initial);
        await Assert.That(context.Conflict.KillCount).IsEqualTo(threshold - 1);
        context.Conflict.AddZoneKill();
        await Assert.That(context.Conflict.CurrentZoneState).IsEqualTo(initial);
        await Assert.That(context.Conflict.KillCount).IsEqualTo(threshold);
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);

        context.Conflict.AddZoneKill();

        var snapshot = context.Conflict.GetSnapshot();
        await Assert.That(snapshot.State).IsEqualTo(next);
        await Assert.That(snapshot.KillCount).IsEqualTo(next == ZoneConflictType.Conflict ? 0u : threshold + 1);
        await Assert.That(snapshot.NextStateTime).IsEqualTo(next == ZoneConflictType.Conflict
            ? Start.UtcDateTime.AddMinutes(5)
            : DateTime.MinValue);
        await Assert.That(context.Broadcasts.Last()).IsEqualTo(snapshot);
    }

    [Test]
    public async Task AddZoneKill_LargeBatch_TraversesCumulativeStages()
    {
        var context = CreateContext();
        context.Conflict.Restore(null);

        context.Conflict.AddZoneKill(251);

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Conflict, 0, Start.UtcDateTime.AddMinutes(5)));
        await Assert.That(context.Broadcasts.Last()).IsEqualTo(context.Conflict.GetSnapshot());
    }

    [Test]
    public async Task CheckTimer_KillDrivenCycle_ReturnsToFreshTension()
    {
        var context = CreateContext();
        context.Conflict.Restore(null);
        context.Conflict.AddZoneKill(251);

        context.Clock.Advance(TimeSpan.FromMinutes(5));
        context.Conflict.CheckTimer();
        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.War, 0, Start.UtcDateTime.AddMinutes(85)));

        context.Clock.Advance(TimeSpan.FromMinutes(80));
        context.Conflict.CheckTimer();
        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Peace, 0, Start.UtcDateTime.AddMinutes(205)));

        context.Clock.Advance(TimeSpan.FromMinutes(120));
        context.Conflict.CheckTimer();
        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Tension, 0, DateTime.MinValue));
        await Assert.That(context.Broadcasts.Select(snapshot => snapshot.State).SequenceEqual(
            [ZoneConflictType.Conflict, ZoneConflictType.War, ZoneConflictType.Peace, ZoneConflictType.Tension])).IsTrue();
    }

    [Test]
    public async Task CheckTimer_DelayedTick_UsesPriorDeadlinesForEveryPhase()
    {
        var context = CreateContext();
        context.Conflict.Restore(null);
        context.Conflict.AddZoneKill(251);

        context.Clock.Advance(TimeSpan.FromMinutes(95));
        context.Conflict.CheckTimer();

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Peace, 0, Start.UtcDateTime.AddMinutes(205)));
    }

    [Test]
    public async Task CheckTimer_ZeroPeaceDuration_SkipsPeaceInTimedCycle()
    {
        var context = CreateContext(killDriven: false);
        context.Conflict.PeaceMin = 0;
        context.Conflict.Restore(null);

        context.Clock.Advance(TimeSpan.FromMinutes(85));
        context.Conflict.CheckTimer();

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Conflict, 0, Start.UtcDateTime.AddMinutes(90)));
        await Assert.That(context.Broadcasts.Any(snapshot => snapshot.State == ZoneConflictType.Peace)).IsFalse();
    }

    [Test]
    [Arguments(ZoneConflictType.Tension, 17)]
    [Arguments(ZoneConflictType.Danger, 85)]
    [Arguments(ZoneConflictType.Dispute, 110)]
    [Arguments(ZoneConflictType.Unrest, 150)]
    [Arguments(ZoneConflictType.Crisis, 200)]
    [Arguments(ZoneConflictType.Conflict, 0)]
    [Arguments(ZoneConflictType.War, 0)]
    [Arguments(ZoneConflictType.Peace, 0)]
    public async Task Restore_SavedSnapshot_PreservesStageCountAndExactDeadline(ZoneConflictType state, uint kills)
    {
        var context = CreateContext();
        var deadline = state >= ZoneConflictType.Conflict
            ? Start.UtcDateTime.AddMinutes(2).AddMilliseconds(123)
            : DateTime.MinValue;
        var saved = new ZoneConflictSnapshot(state, kills, deadline);
        context.Conflict.Restore(saved);
        var loaded = CreateContext();

        loaded.Conflict.Restore(context.Conflict.GetSnapshot());

        await Assert.That(loaded.Conflict.GetSnapshot()).IsEqualTo(saved);
        await Assert.That(loaded.Conflict.CurrentZoneState).IsEqualTo(state);
        await Assert.That(loaded.Conflict.KillCount).IsEqualTo(kills);
        await Assert.That(loaded.Conflict.NextStateTime).IsEqualTo(deadline);
        await Assert.That(context.Broadcasts.Count + loaded.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Restore_OverdueTimedPhases_CatchesUpWithoutBroadcastingHistory()
    {
        var context = CreateContext();

        context.Conflict.Restore(new ZoneConflictSnapshot(
            ZoneConflictType.Conflict, 0, Start.UtcDateTime.AddMinutes(-100)));

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Peace, 0, Start.UtcDateTime.AddMinutes(100)));
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Restore_CompletedKillDrivenCycle_WaitsForNewKills()
    {
        var context = CreateContext();

        context.Conflict.Restore(new ZoneConflictSnapshot(
            ZoneConflictType.Conflict, 0, Start.UtcDateTime.AddMinutes(-205)));

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Tension, 0, DateTime.MinValue));
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Restore_ManyTimedOnlyCycles_RetainsOriginalCycleAlignment()
    {
        var context = CreateContext(killDriven: false);
        const int completedCycles = 100_000;
        context.Clock.Advance(TimeSpan.FromMinutes(205L * completedCycles + 115));

        context.Conflict.Restore(new ZoneConflictSnapshot(
            ZoneConflictType.Conflict, 0, Start.UtcDateTime.AddMinutes(5)));

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(new ZoneConflictSnapshot(
            ZoneConflictType.Peace, 0, Start.UtcDateTime.AddMinutes(205L * completedCycles + 205)));
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AddZoneKill_ConcurrentCreditAndTimerChecks_DoNotLoseCounts()
    {
        var context = CreateContext();
        for (var index = 0; index < context.Conflict.NumKills.Length; index++)
            context.Conflict.NumKills[index] = (index + 1) * 10_000;
        context.Conflict.Restore(null);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var count = 0; count < 64; count++)
            {
                context.Conflict.AddZoneKill();
                context.Conflict.CheckTimer();
                var snapshot = context.Conflict.GetSnapshot();
                if (snapshot.State != ZoneConflictType.Tension || snapshot.NextStateTime != DateTime.MinValue)
                    throw new InvalidOperationException("Inconsistent escalation snapshot.");
            }
        })));

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.Tension, 1024, DateTime.MinValue));
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CheckTimer_ConcurrentExpiredChecks_AdvanceOnlyOnce()
    {
        var context = CreateContext(killDriven: false);
        context.Conflict.Restore(null);
        context.Clock.Advance(TimeSpan.FromMinutes(5));

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(context.Conflict.CheckTimer)));

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(
            new ZoneConflictSnapshot(ZoneConflictType.War, 0, Start.UtcDateTime.AddMinutes(85)));
        await Assert.That(context.Broadcasts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckTimer_BroadcastFailure_DoesNotStopFutureTransitions()
    {
        var clock = new FakeTimeProvider(Start);
        var attempts = 0;
        var conflict = CreateConflict(clock, _ =>
        {
            if (++attempts == 1)
                throw new IOException("Injected broadcast failure.");
        });
        conflict.Restore(null);
        conflict.AddZoneKill(251);

        clock.Advance(TimeSpan.FromMinutes(5));
        conflict.CheckTimer();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.War);
        await Assert.That(conflict.NextStateTime).IsEqualTo(Start.UtcDateTime.AddMinutes(85));
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task ClosedZone_KillsAndTimerChecks_DoNotAdvance()
    {
        var context = CreateContext();
        context.Conflict.Closed = true;
        context.Conflict.Restore(null);
        var initial = context.Conflict.GetSnapshot();

        context.Conflict.AddZoneKill(251);
        context.Clock.Advance(TimeSpan.FromDays(1));
        context.Conflict.CheckTimer();

        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(initial);
        await Assert.That(context.Broadcasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RecordZoneConflictKill_ThresholdCrossing_ReturnsPreCreditHonorState()
    {
        var context = CreateContext();
        context.Conflict.Restore(new ZoneConflictSnapshot(ZoneConflictType.Crisis, 250, DateTime.MinValue));

        var honorState = Character.RecordZoneConflictKill(context.Conflict, true);

        await Assert.That(honorState).IsEqualTo(ZoneConflictType.Crisis);
        await Assert.That(context.Conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);
        await Assert.That(context.Conflict.KillCount).IsEqualTo(0u);
    }

    [Test]
    public async Task RecordZoneConflictKill_ConcurrentThresholdCrossing_OnlyCrossingKillUsesCrisis()
    {
        var context = CreateContext();
        context.Conflict.Restore(new ZoneConflictSnapshot(ZoneConflictType.Crisis, 250, DateTime.MinValue));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deaths = Enumerable.Range(0, 64).Select(async _ =>
        {
            await start.Task;
            return Character.RecordZoneConflictKill(context.Conflict, true);
        }).ToArray();

        start.SetResult();
        var honorStates = await Task.WhenAll(deaths);

        await Assert.That(honorStates.Count(state => state == ZoneConflictType.Crisis)).IsEqualTo(1);
        await Assert.That(honorStates.Count(state => state == ZoneConflictType.Conflict)).IsEqualTo(63);
        await Assert.That(context.Conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);
        await Assert.That(context.Broadcasts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RecordZoneConflictKill_NonqualifyingDeath_DoesNotIncrement()
    {
        var context = CreateContext();
        context.Conflict.Restore(new ZoneConflictSnapshot(ZoneConflictType.Crisis, 250, DateTime.MinValue));
        var before = context.Conflict.GetSnapshot();

        var honorState = Character.RecordZoneConflictKill(context.Conflict, false);

        await Assert.That(honorState).IsEqualTo(ZoneConflictType.Crisis);
        await Assert.That(context.Conflict.GetSnapshot()).IsEqualTo(before);
    }

    [Test]
    public async Task RecordZoneConflictKill_NoConflict_ReturnsPeace()
    {
        await Assert.That(Character.RecordZoneConflictKill(null, true)).IsEqualTo(ZoneConflictType.Peace);
    }

    private static ConflictContext CreateContext(bool killDriven = true)
    {
        var clock = new FakeTimeProvider(Start);
        var broadcasts = new List<ZoneConflictSnapshot>();
        var conflict = CreateConflict(clock, broadcasts.Add, killDriven);
        return new ConflictContext(clock, conflict, broadcasts);
    }

    private static ZoneConflict CreateConflict(TimeProvider clock, Action<ZoneConflictSnapshot> broadcast, bool killDriven = true)
    {
        var conflict = new ZoneConflict(clock, broadcast)
        {
            ZoneGroupId = 17,
            ConflictMin = 5,
            WarMin = 80,
            PeaceMin = 120
        };
        if (killDriven)
            new[] { 70, 100, 140, 190, 250 }.CopyTo(conflict.NumKills, 0);
        return conflict;
    }

    private sealed record ConflictContext(FakeTimeProvider Clock, ZoneConflict Conflict, List<ZoneConflictSnapshot> Broadcasts);
}
