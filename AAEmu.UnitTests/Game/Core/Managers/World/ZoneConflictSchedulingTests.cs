using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Tasks.Zones;

using Microsoft.Extensions.Time.Testing;

using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed class ZoneConflictSchedulingTests
{
    [Test]
    public async Task Initialize_RepeatedCall_SchedulesOneRepeatingCheckPerOpenZone()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var tasks = new RecordingTaskManager();
        var manager = new ZoneManager(Mock.Of<IWorldManager>().Object, tasks);
        var escalation = CreateConflict(clock, 17);
        new[] { 70, 100, 140, 190, 250 }.CopyTo(escalation.NumKills, 0);
        var timed = CreateConflict(clock, 30);
        var closed = CreateConflict(clock, 78);
        closed.Closed = true;
        foreach (var conflict in new[] { escalation, timed, closed })
            conflict.Restore(null);
        SetConflicts(manager, escalation, timed, closed);

        manager.Initialize();
        manager.Initialize();

        await Assert.That(tasks.Scheduled.Count).IsEqualTo(2);
        await Assert.That(tasks.Scheduled.All(schedule => schedule.Task is ZoneStateChangeTask)).IsTrue();
        await Assert.That(tasks.Scheduled.All(schedule => schedule.RepeatInterval == TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(tasks.Scheduled.All(schedule => schedule.Count == -1)).IsTrue();
        await Assert.That(escalation.CurrentZoneState).IsEqualTo(ZoneConflictType.Tension);
        await Assert.That(closed.CurrentZoneState).IsEqualTo(ZoneConflictType.Tension);
    }

    [Test]
    public async Task Initialize_DeadlineElapsedAfterRestore_ChecksBeforeFirstScheduledTick()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var tasks = new RecordingTaskManager();
        var manager = new ZoneManager(Mock.Of<IWorldManager>().Object, tasks);
        var conflict = CreateConflict(clock, 30);
        conflict.Restore(null);
        SetConflicts(manager, conflict);
        clock.Advance(TimeSpan.FromMinutes(5));

        manager.Initialize();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.War);
        await Assert.That(conflict.NextStateTime).IsEqualTo(clock.GetUtcNow().UtcDateTime.AddMinutes(80));
        await Assert.That(tasks.Scheduled.Count).IsEqualTo(1);
    }

    private static ZoneConflict CreateConflict(TimeProvider clock, ushort id)
    {
        return new ZoneConflict(clock, _ => { })
        {
            ZoneGroupId = id,
            ConflictMin = 5,
            WarMin = 80,
            PeaceMin = 0
        };
    }

    private static void SetConflicts(ZoneManager manager, params ZoneConflict[] conflicts)
    {
        typeof(ZoneManager).GetField("_conflicts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, conflicts.ToDictionary(conflict => conflict.ZoneGroupId));
    }

    private sealed record ScheduledTask(GameTask Task, TimeSpan? RepeatInterval, int Count);

    private sealed class RecordingTaskManager : ITaskManager
    {
        public List<ScheduledTask> Scheduled { get; } = [];
        public void Initialize() { }
        public void Start() { }
        public void Stop() { }
        public bool Cancel(GameTask task) => true;
        public bool CronSchedule(GameTask task, string cronExpression, TimeSpan? startDelay = null, int count = -1) => true;

        public bool Schedule(GameTask task, TimeSpan? startTime = null, TimeSpan? repeatInterval = null, int count = -1)
        {
            Scheduled.Add(new ScheduledTask(task, repeatInterval, count));
            return true;
        }
    }
}
