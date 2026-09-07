using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Schedules;
using AAEmu.Game.Models.Tasks.World;

using Microsoft.Extensions.Time.Testing;

using GameTask = AAEmu.Game.Models.Tasks.Task;
using ScheduleDayOfWeek = AAEmu.Game.Models.Game.Schedules.DayOfWeek;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class DoodadScheduleLifecycleTests
{
    private const uint TemplateId = 6972;
    private static readonly FieldInfo s_scheduleInstance =
        typeof(Singleton<GameScheduleManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_taskInstance =
        typeof(Singleton<TaskManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private GameScheduleManager _previousScheduleManager;
    private TaskManager _previousTaskManager;
    private FakeTimeProvider _timeProvider;
    private TaskManager _taskManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousScheduleManager = (GameScheduleManager)s_scheduleInstance.GetValue(null);
        _previousTaskManager = (TaskManager)s_taskInstance.GetValue(null);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T03:00:00Z"));
        var schedules = new GameScheduleManager(Mock.Of<IGameDataManager>().Object, _timeProvider);
        schedules.LoadGameSchedules(new Dictionary<int, GameSchedules>
        {
            [1] = new GameSchedules
            {
                Id = 1,
                DayOfWeekId = ScheduleDayOfWeek.Invalid,
                StartTime = 4,
                EndTime = 6
            }
        });
        schedules.LoadGameScheduleDoodads(new Dictionary<int, GameScheduleDoodads>
        {
            [1] = new GameScheduleDoodads { Id = 1, GameScheduleId = 1, DoodadId = (int)TemplateId }
        });
        _taskManager = new TaskManager(Mock.Of<ITickManager>().Object);
        s_scheduleInstance.SetValue(null, schedules);
        s_taskInstance.SetValue(null, _taskManager);
    }

    [After(Test)]
    public void TearDown()
    {
        s_scheduleInstance.SetValue(null, _previousScheduleManager);
        s_taskInstance.SetValue(null, _previousTaskManager);
    }

    [Test]
    public async Task ScheduledDespawn_NextWindow_RecreatesAndSpawnsDoodad()
    {
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        var spawner = new RecordingDoodadSpawner { UnitId = TemplateId };
        var original = (RecordingDoodad)spawner.Spawn(0);
        await Assert.That(original.SpawnCalls).IsEqualTo(1);

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T06:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();

        await Assert.That(original.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsNull();

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-02T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();

        await Assert.That(spawner.SpawnCalls).IsEqualTo(2);
        await Assert.That(spawner.Last).IsNotNull();
        await Assert.That(spawner.Last).IsNotSameReferenceAs(original);
        await Assert.That(((RecordingDoodad)spawner.Last).SpawnCalls).IsEqualTo(1);
        await Assert.That(spawner._spawned.Count).IsEqualTo(1);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoDespawnTask>();
    }

    [Test]
    public async Task InitialSpawn_BeforeWindow_UsesAlreadyCreatedDoodad()
    {
        var spawner = new RecordingDoodadSpawner { UnitId = TemplateId };
        var original = (RecordingDoodad)spawner.Spawn(0);
        await Assert.That(original.SpawnCalls).IsEqualTo(0);

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();

        await Assert.That(spawner.SpawnCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsSameReferenceAs(original);
        await Assert.That(original.SpawnCalls).IsEqualTo(1);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoDespawnTask>();
    }

    private void ExecuteQueued<T>() where T : GameTask
    {
        var task = QueuedTasks().OfType<T>().Single();
        _taskManager.Cancel(task);
        task.Execute();
    }

    private ICollection<GameTask> QueuedTasks()
    {
        var queue = (ConcurrentDictionary<uint, GameTask>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_taskManager)!;
        return queue.Values;
    }

    private sealed class RecordingDoodadSpawner : DoodadSpawner
    {
        public int SpawnCalls { get; private set; }

        public override Doodad Spawn(uint objId)
        {
            SpawnCalls++;
            _spawned = [];
            Last = new RecordingDoodad { TemplateId = UnitId, Respawn = DateTime.UnixEpoch, Spawner = this };
            DoSpawn();
            return Last;
        }
    }

    private sealed class RecordingDoodad : Doodad
    {
        public int SpawnCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public override void Spawn()
        {
            SpawnCalls++;
        }

        public override void Delete()
        {
            DeleteCalls++;
        }
    }
}
