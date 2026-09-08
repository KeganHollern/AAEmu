using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Schedules;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Tasks.World;
using AAEmu.Game.Models.Tasks.Doodads;

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
    private static readonly FieldInfo s_objectIdInstance =
        typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly FieldInfo s_worldInstance =
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private WorldManager _previousWorldManager;
    private WorldManager _worldManager;
    private GameScheduleManager _previousScheduleManager;
    private TaskManager _previousTaskManager;
    private ObjectIdManager _previousObjectIds;
    private FakeTimeProvider _timeProvider;
    private GameScheduleManager _schedules;
    private TaskManager _taskManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = (WorldManager)s_worldInstance.GetValue(null);
        _worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        s_worldInstance.SetValue(null, _worldManager);
        _previousScheduleManager = (GameScheduleManager)s_scheduleInstance.GetValue(null);
        _previousTaskManager = (TaskManager)s_taskInstance.GetValue(null);
        _previousObjectIds = (ObjectIdManager)s_objectIdInstance.GetValue(null);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T03:00:00Z"));
        _schedules = new GameScheduleManager(Mock.Of<IGameDataManager>().Object, _timeProvider);
        SetSchedules(new GameSchedules { Id = 1, DayOfWeekId = ScheduleDayOfWeek.Invalid, StartTime = 4, EndTime = 6 });
        _taskManager = new TaskManager(Mock.Of<ITickManager>().Object);
        var objectIds = new ObjectIdManager();
        objectIds.Initialize();
        s_scheduleInstance.SetValue(null, _schedules);
        s_taskInstance.SetValue(null, _taskManager);
        s_objectIdInstance.SetValue(null, objectIds);
    }

    [After(Test)]
    public void TearDown()
    {
        s_worldInstance.SetValue(null, _previousWorldManager);
        s_scheduleInstance.SetValue(null, _previousScheduleManager);
        s_taskInstance.SetValue(null, _previousTaskManager);
        s_objectIdInstance.SetValue(null, _previousObjectIds);
    }

    [Test]
    public async Task TwoCompleteCycles_CreateFreshOccurrencesAndObjectIds()
    {
        var spawner = CreateSpawner();
        var initial = spawner.Spawn(0);
        await Assert.That(initial).IsNull();
        await Assert.That(spawner.Created).IsEmpty();

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();
        var first = (RecordingDoodad)spawner.Last;
        await Assert.That(first.SpawnCalls).IsEqualTo(1);

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T06:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();
        await Assert.That(first.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsNull();
        await Assert.That(spawner._spawned).IsEmpty();

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-02T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();
        var second = (RecordingDoodad)spawner.Last;
        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(second.ObjId).IsNotEqualTo(first.ObjId);
        await Assert.That(second.SpawnCalls).IsEqualTo(1);
        await Assert.That(spawner._spawned).HasSingleItem();

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-02T06:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();
        await Assert.That(second.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsNull();
        await Assert.That(spawner._spawned).IsEmpty();
        await Assert.That(spawner.Created.Count).IsEqualTo(2);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoSpawnTask>();
    }

    [Test]
    public async Task InitialSpawn_BeforeWindow_DoesNotAllocateOrInitializeAnOccurrence()
    {
        var spawner = CreateSpawner();
        await Assert.That(spawner.Spawn(0)).IsNull();
        await Assert.That(spawner.Created).IsEmpty();

        spawner.DoSpawn();
        await Assert.That(QueuedTasks().Count).IsEqualTo(1);
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();

        await Assert.That(spawner.Created.Count).IsEqualTo(1);
        await Assert.That(spawner.Created[0].SpawnCalls).IsEqualTo(1);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoDespawnTask>();
    }

    [Test]
    [Arguments(3, false)]
    [Arguments(4, true)]
    [Arguments(6, false)]
    public async Task OwnedSpawn_UsesTheSameWindowBeforeCreatingAnOccurrence(int hour, bool active)
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 1, hour, 0, 0, TimeSpan.Zero));
        var spawner = CreateSpawner();
        _worldManager.TryAddCharacter(new Character(null) { ObjId = 123, ParentWorld = spawner.ParentWorld });

        var doodad = spawner.Spawn(0, 42, 123);

        await Assert.That(doodad != null).IsEqualTo(active);
        await Assert.That(spawner.Created.Count).IsEqualTo(active ? 1 : 0);
        await Assert.That(spawner._spawned.Count).IsEqualTo(active ? 1 : 0);
    }

    [Test]
    public async Task OverlappingWindows_KeepOccurrenceUntilTheLastWindowEnds()
    {
        SetSchedules(
            new GameSchedules { Id = 1, StartTime = 4, EndTime = 6 },
            new GameSchedules { Id = 2, StartTime = 5, EndTime = 7 });
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        var spawner = CreateSpawner();
        var doodad = (RecordingDoodad)spawner.Spawn(0);

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T06:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();
        await Assert.That(spawner.Last).IsSameReferenceAs(doodad);
        await Assert.That(doodad.DeleteCalls).IsEqualTo(0);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoDespawnTask>();

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T07:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();
        await Assert.That(doodad.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsNull();
    }

    [Test]
    public async Task EndedAbsoluteWindow_DoesNotCreateAnOccurrenceOrQueueAnotherStart()
    {
        SetSchedules(new GameSchedules
        {
            Id = 1, StYear = 2026, StMonth = 9, StDay = 1, StHour = 1,
            EdYear = 2026, EdMonth = 9, EdDay = 1, EdHour = 2
        });
        var spawner = CreateSpawner();

        await Assert.That(spawner.Spawn(0)).IsNull();
        await Assert.That(spawner.Created).IsEmpty();
        await Assert.That(QueuedTasks()).IsEmpty();
    }

    [Test]
    public async Task DuplicateSpawnAndOldDespawnCallbacks_DoNotAffectTheCurrentOccurrence()
    {
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        var spawner = CreateSpawner();
        var first = (RecordingDoodad)spawner.Spawn(0);
        var oldDespawn = QueuedTasks().Single();
        spawner.DoSpawn();
        spawner.Spawn(0);
        await Assert.That(first.SpawnCalls).IsEqualTo(1);
        await Assert.That(spawner._spawned).HasSingleItem();
        await Assert.That(QueuedTasks().Count).IsEqualTo(1);

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T06:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-02T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();
        var second = (RecordingDoodad)spawner.Last;
        oldDespawn.Execute();

        await Assert.That(second.DeleteCalls).IsEqualTo(0);
        await Assert.That(first.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsSameReferenceAs(second);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoDespawnTask>();
    }

    [Test]
    public async Task DetachedWorld_DelayedSpawnCannotRecreateObjects()
    {
        var spawner = CreateSpawner();
        spawner.Spawn(0);
        spawner.ParentWorld = null;
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoSpawnTask>();

        await Assert.That(spawner.Created).IsEmpty();
        await Assert.That(QueuedTasks()).IsEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(5)]
    public async Task FinalPhase_OrdinaryRespawn_CreatesFreshOccurrenceThroughTheRealSpawner(int after)
    {
        _schedules.LoadGameScheduleDoodads([]);
        var spawner = CreateSpawner();
        var first = (RecordingDoodad)spawner.Spawn(0);
        var final = new DoodadFuncFinal { After = after, Respawn = true, MinTime = 1, MaxTime = 1 };
        final.Use(null, first);

        var callback = QueuedTasks().OfType<DoodadFuncFinalTask>().Single();
        ExecuteQueued<DoodadFuncFinalTask>();
        await Assert.That(spawner.Last).IsNull();
        await Assert.That(spawner._spawned).IsEmpty();
        await Assert.That(first.FuncTask).IsSameReferenceAs(callback);

        ExecuteQueued<DoodadFuncFinalTask>();
        var second = (RecordingDoodad)spawner.Last;
        callback.Execute();

        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(second.ObjId).IsNotEqualTo(first.ObjId);
        await Assert.That(second.SpawnCalls).IsEqualTo(1);
        await Assert.That(first.SpawnCalls).IsEqualTo(1);
        await Assert.That(first.FuncTask).IsNull();
        await Assert.That(spawner._spawned).HasSingleItem();
        await Assert.That(spawner.Created.Count).IsEqualTo(2);
        await Assert.That(QueuedTasks()).IsEmpty();
    }

    [Test]
    public async Task ScheduledExpiry_DisarmsAlreadyDispatchedPhaseCallback()
    {
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        var spawner = CreateSpawner();
        var first = (RecordingDoodad)spawner.Spawn(0);
        var phaseCallback = new DoodadFuncFinalTask(null, first, 0, true, 1);
        first.FuncTask = phaseCallback;
        _taskManager.Schedule(phaseCallback, TimeSpan.FromHours(4));

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T06:00:00Z"));
        ExecuteQueued<DoodadSpawnerDoDespawnTask>();
        phaseCallback.Execute();

        await Assert.That(first.FuncTask).IsNull();
        await Assert.That(first.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsNull();
        await Assert.That(spawner.Created.Count).IsEqualTo(1);
        await Assert.That(QueuedTasks().Single()).IsTypeOf<DoodadSpawnerDoSpawnTask>();
    }

    [Test]
    public async Task QueuedRemoval_StaleCallbackCannotReleaseAnIdReusedByAnotherObject()
    {
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T04:00:00Z"));
        var spawner = CreateSpawner();
        var doodad = (RecordingDoodad)spawner.Spawn(0);
        spawner.ParentWorld.SpawnManager.DespawnObject(doodad);
        var reusedId = ObjectIdManager.Instance.GetNextId();
        await Assert.That(reusedId).IsEqualTo(doodad.ObjId);

        spawner.ParentWorld.SpawnManager.DespawnObject(doodad);
        await Assert.That(doodad.DeleteCalls).IsEqualTo(1);
        await Assert.That(ObjectIdManager.Instance.GetNextId()).IsNotEqualTo(reusedId);
    }

    [Test]
    [Arguments(0)]
    [Arguments(60)]
    public async Task OrdinaryRetirement_ReleasesOldIdAndPreservesRequestedRespawn(int respawnTime)
    {
        SetSchedules();
        var spawner = CreateSpawner();
        spawner.RespawnTime = respawnTime;
        var first = (RecordingDoodad)spawner.Spawn(0);

        spawner.DecreaseCount(first);
        spawner.DecreaseCount(first);
        await Assert.That(first.DeleteCalls).IsEqualTo(1);
        await Assert.That(spawner.Last).IsNull();
        await Assert.That(spawner._spawned).IsEmpty();
        var reusedId = ObjectIdManager.Instance.GetNextId();
        await Assert.That(reusedId).IsEqualTo(first.ObjId);
        var respawns = (HashSet<GameObject>)typeof(SpawnManager)
            .GetProperty("Respawns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(spawner.ParentWorld.SpawnManager)!;
        await Assert.That(respawns.Count).IsEqualTo(respawnTime > 0 ? 1 : 0);

        if (respawnTime > 0)
        {
            spawner.Respawn(first);
            await Assert.That(spawner.Last).IsNotSameReferenceAs(first);
            await Assert.That(spawner.Last.ObjId).IsNotEqualTo(first.ObjId);
            await Assert.That(spawner.Created.Count).IsEqualTo(2);
        }
        await Assert.That(QueuedTasks()).IsEmpty();
    }

    [Test]
    public async Task PhaseCallback_DuringUse_CannotInvertTheDespawnLockOrder()
    {
        var skillField = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var doodadField = typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousSkills = skillField.GetValue(null);
        var previousDoodads = doodadField.GetValue(null);
        using var insideUse = new ManualResetEventSlim();
        using var phaseStarted = new ManualResetEventSlim();
        using var insidePhase = new ManualResetEventSlim();
        var phaseEnteredDuringUse = false;
        try
        {
            SetSchedules();
            var spawner = CreateSpawner();
            var doodad = (RecordingDoodad)spawner.Spawn(0);
            var phaseTask = new DoodadFuncFinalTask(null, doodad, 0, true, 1);
            doodad.FuncTask = phaseTask;
            var skills = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
            typeof(SkillManager).GetField("_skills", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(skills, new Dictionary<uint, SkillTemplate>());
            skillField.SetValue(null, skills);
            var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
                Mock.Of<IItemManager>().Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
                Mock.Of<ISusManager>().Object);
            var function = new DoodadFunc { FuncId = 1, FuncType = "DoodadFuncLootPack" };
            typeof(DoodadManager).GetField("_funcsByGroups", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, new Dictionary<uint, List<DoodadFunc>> { [0] = [function] });
            typeof(DoodadManager).GetField("_funcTemplates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
                {
                    [function.FuncType] = new() { [1] = new InteractionTemplate(() =>
                    {
                        insideUse.Set();
                        if (!phaseStarted.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("The phase callback did not start.");
                        phaseEnteredDuringUse = insidePhase.Wait(TimeSpan.FromMilliseconds(100));
                        // Avoid stranding test threads if the old inverse lock order returns.
                        if (!phaseEnteredDuringUse)
                            spawner.Despawn(doodad);
                    }) }
                });
            doodadField.SetValue(null, manager);
            var use = Task.Run(() => doodad.Use(new Character(new UnitCustomModelParams())));
            var phase = Task.Run(() =>
            {
                if (!insideUse.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The interaction did not start.");
                phaseStarted.Set();
                spawner.ExecutePhaseTask(doodad, phaseTask, () =>
                {
                    insidePhase.Set();
                    doodad.DoChangePhase(null, 0);
                });
            });
            await Task.WhenAll(use, phase).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(phaseEnteredDuringUse).IsFalse();
            await Assert.That(doodad.DeleteCalls).IsEqualTo(1);
            await Assert.That(spawner.Last).IsNull();
        }
        finally
        {
            skillField.SetValue(null, previousSkills);
            doodadField.SetValue(null, previousDoodads);
        }
    }

    private sealed class InteractionTemplate(Action action) : DoodadFuncTemplate
    {
        public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0) => action();
    }

    private void SetSchedules(params GameSchedules[] schedules)
    {
        _schedules.LoadGameSchedules(schedules.ToDictionary(s => s.Id));
        _schedules.LoadGameScheduleDoodads(schedules.ToDictionary(s => s.Id,
            s => new GameScheduleDoodads { Id = s.Id, GameScheduleId = s.Id, DoodadId = (int)TemplateId }));
    }

    private RecordingDoodadSpawner CreateSpawner()
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        world.SpawnManager = new SpawnManager(world);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worldManager)!;
        worlds[world.Id] = world;
        return new RecordingDoodadSpawner
        {
            UnitId = TemplateId,
            ParentWorld = world,
            Position = new WorldSpawnPosition { WorldId = 1, X = 100, Y = 100, Z = 10 }
        };
    }

    private void ExecuteQueued<T>() where T : GameTask
    {
        var task = QueuedTasks().OfType<T>().Single();
        _taskManager.Cancel(task);
        task.Execute();
    }

    private ICollection<GameTask> QueuedTasks()
    {
        return ((ConcurrentDictionary<uint, GameTask>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_taskManager)!).Values;
    }

    private sealed class RecordingDoodadSpawner : DoodadSpawner
    {
        public List<RecordingDoodad> Created { get; } = [];

        protected override Doodad CreateDoodad(uint objectId, uint templateId, GameObject owner)
        {
            var doodad = new RecordingDoodad { ObjId = objectId, TemplateId = templateId, ParentWorld = ParentWorld };
            Created.Add(doodad);
            return doodad;
        }
    }

    private sealed class RecordingDoodad : Doodad
    {
        public int SpawnCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public override void Spawn() => SpawnCalls++;
        public override void Delete() => DeleteCalls++;
    }
}
