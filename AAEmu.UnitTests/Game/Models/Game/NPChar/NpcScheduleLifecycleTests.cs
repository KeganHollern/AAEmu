using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Schedules;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Tasks.World;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcScheduleLifecycleTests
{
    private const uint SpawnerId = 14982;
    private const uint TemplateId = 100;
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private static readonly FieldInfo s_objectIdInstance =
        typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private ObjectIdManager _previousObjectIds;
    private FakeTimeProvider _timeProvider;
    private GameScheduleManager _schedules;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        _previousObjectIds = (ObjectIdManager)s_objectIdInstance.GetValue(null);
        var objectIds = new ObjectIdManager();
        objectIds.Initialize();
        s_objectIdInstance.SetValue(null, objectIds);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T03:00:00Z"));
        _schedules = new GameScheduleManager(Mock.Of<IGameDataManager>().Object, _timeProvider);
        SetInstance(_schedules);
        SetInstance(new NpcGameData());
        var worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetInstance(worldManager);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        _world.SpawnManager = new SpawnManager(_world);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worldManager)!;
        worlds[_world.Id] = _world;
        worldManager.TryAddCharacter(new Character(null) { Id = 1, ObjId = 1, ParentWorld = _world });
        SetSchedules(new GameSchedules { Id = 1, StartTime = 4, EndTime = 6 });
    }

    [After(Test)]
    public void TearDown()
    {
        s_objectIdInstance.SetValue(null, _previousObjectIds);
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
        _previousInstances.Clear();
    }

    [Test]
    [Arguments(3, false, false)]
    [Arguments(3, true, false)]
    [Arguments(4, false, true)]
    [Arguments(4, true, true)]
    [Arguments(6, false, false)]
    [Arguments(6, true, false)]
    public async Task AbsoluteWindow_SpawnAndRemovalAreComplementary(int hour, bool inCombat, bool active)
    {
        SetSchedules(new GameSchedules
        {
            Id = 1, StYear = 2026, StMonth = 9, StDay = 1, StHour = 4,
            EdYear = 2026, EdMonth = 9, EdDay = 1, EdHour = 6
        });
        var spawner = CreateSpawner();
        var npc = spawner.SeedOccurrence(inCombat);
        SetHour(1, hour);

        await Assert.That(spawner.IsSpawningScheduleEnabled()).IsEqualTo(active);
        await Assert.That(spawner.CanDespawnNpcs()).IsEqualTo(!active);
        spawner.Update();
        var firstDeadline = npc.Despawn;
        spawner.Update();
        spawner.DoDespawns([npc]);

        await Assert.That(Pending("Despawns").Count).IsEqualTo(active ? 0 : 1);
        await Assert.That(npc.Despawn).IsEqualTo(firstDeadline);
        await Assert.That(npc.Despawn == DateTime.MinValue).IsEqualTo(active);
        await Assert.That(Pending("Respawns")).IsEmpty();
        await Assert.That(spawner.Created.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(3)]
    [Arguments(6)]
    public async Task EverySpawnEntryPoint_RejectsInactiveWindows(int hour)
    {
        var spawner = CreateSpawner();
        SetHour(1, hour);

        spawner.SpawnAll();
        spawner.SpawnAll(true);
        await Assert.That(spawner.Spawn(0)).IsNull();
        await Assert.That(spawner.ForceSpawn(0)).IsNull();
        spawner.DoSpawn();
        spawner.DoEventSpawn();
        new NpcSpawnerDoSpawnTask(spawner).Execute();

        await Assert.That(spawner.Created).IsEmpty();
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
    }

    [Test]
    [Arguments("SpawnAll")]
    [Arguments("SpawnAllBeginning")]
    [Arguments("Spawn")]
    [Arguments("ForceSpawn")]
    [Arguments("DoSpawn")]
    [Arguments("DoEventSpawn")]
    [Arguments("Task")]
    public async Task EverySpawnEntryPoint_AcceptsActiveWindows(string entryPoint)
    {
        var spawner = CreateSpawner();
        SetHour(1, 4);

        switch (entryPoint)
        {
            case "SpawnAll": spawner.SpawnAll(); break;
            case "SpawnAllBeginning": spawner.SpawnAll(true); break;
            case "Spawn": spawner.Spawn(0); break;
            case "ForceSpawn": spawner.ForceSpawn(0); break;
            case "DoSpawn": spawner.DoSpawn(); break;
            case "DoEventSpawn": spawner.DoEventSpawn(); break;
            case "Task": new NpcSpawnerDoSpawnTask(spawner).Execute(); break;
        }

        await Assert.That(spawner.Created).HasSingleItem();
        await Assert.That(spawner.SpawnedNpcs[SpawnerId]).HasSingleItem();
    }

    [Test]
    public async Task TwoCompleteCycles_UpdateSpawnsAgainWithoutLatchedScheduleFlags()
    {
        var spawner = CreateSpawner();
        for (var day = 1; day <= 2; day++)
        {
            SetHour(day, 3);
            spawner.Update();
            await Assert.That(spawner.IsSpawningScheduleEnabled()).IsFalse();
            await Assert.That(spawner.SpawnedNpcs).IsEmpty();

            SetHour(day, 4);
            await Assert.That(spawner.IsSpawningScheduleEnabled()).IsTrue();
            await Assert.That(spawner.IsSpawningScheduleEnabled()).IsTrue();
            spawner.Update();
            spawner.Update();
            var npc = spawner.Created.Last();
            await Assert.That(spawner.Created.Count).IsEqualTo(day);
            await Assert.That(spawner.SpawnedNpcs[SpawnerId]).HasSingleItem();

            SetHour(day, 6);
            spawner.Update();
            await Assert.That(Pending("Despawns").Single()).IsSameReferenceAs(npc);
            _world.SpawnManager.DespawnObject(npc);
            await Assert.That(npc.DeleteCalls).IsEqualTo(1);
            await Assert.That(spawner.SpawnedNpcs).IsEmpty();
            await Assert.That(Pending("Respawns")).IsEmpty();
        }
        await Assert.That(spawner.Created[0]).IsNotSameReferenceAs(spawner.Created[1]);
    }

    [Test]
    public async Task OverlappingWindows_KeepOccurrenceThroughTheLastEnd()
    {
        SetSchedules(new GameSchedules { Id = 1, StartTime = 4, EndTime = 6 },
            new GameSchedules { Id = 2, StartTime = 5, EndTime = 7 });
        var spawner = CreateSpawner();
        SetHour(1, 4);
        spawner.Update();
        var npc = spawner.Created.Single();
        npc.IsInBattle = true;

        SetHour(1, 6);
        spawner.Update();
        spawner.DoDespawns([npc]);
        await Assert.That(spawner.CanDespawnNpcs()).IsFalse();
        await Assert.That(Pending("Despawns")).IsEmpty();
        await Assert.That(spawner.Created).HasSingleItem();

        SetHour(1, 7);
        spawner.Update();
        await Assert.That(Pending("Despawns").Single()).IsSameReferenceAs(npc);
        await Assert.That(Pending("Respawns")).IsEmpty();
    }

    [Test]
    public async Task PendingDeathRespawn_CannotResurrectAnExpiredWindow()
    {
        var spawner = CreateSpawner();
        SetHour(1, 4);
        spawner.Update();
        var npc = spawner.Created.Single();
        npc.Hp = 0;
        spawner.DoDespawn(npc);
        await Assert.That(Pending("Respawns").Single()).IsSameReferenceAs(npc);

        SetHour(1, 6);
        spawner.Update();
        _world.SpawnManager.DespawnObject(npc);
        _world.SpawnManager.RespawnObject(npc);
        spawner.Update();
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
        await Assert.That(spawner.Created).HasSingleItem();

        SetHour(2, 4);
        spawner.Update();
        await Assert.That(spawner.Created.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PreviousWindowRespawn_CannotReleaseTheCurrentOccurrencesDelay()
    {
        var spawner = CreateSpawner();
        SetHour(1, 4);
        spawner.Update();
        var first = spawner.Created.Single();
        first.Hp = 0;
        spawner.DoDespawn(first);
        _world.SpawnManager.DespawnObject(first);
        SetHour(1, 6);
        spawner.Update();

        SetHour(2, 4);
        spawner.Update();
        var second = spawner.Created.Last();
        second.Hp = 0;
        spawner.DoDespawn(second);
        _world.SpawnManager.DespawnObject(second);
        _world.SpawnManager.RespawnObject(first);
        spawner.Update();

        await Assert.That(spawner.Created.Count).IsEqualTo(2);
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
        await Assert.That(Pending("Respawns").Single()).IsSameReferenceAs(second);

        _world.SpawnManager.RespawnObject(second);
        spawner.Update();
        await Assert.That(spawner.Created.Count).IsEqualTo(3);
        await Assert.That(Pending("Respawns")).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ExplicitReset_DiscardsOldPendingRespawnOwnership(bool reactivate)
    {
        var spawner = CreateSpawner();
        SetHour(1, 4);
        spawner.Update();
        var first = spawner.Created.Single();
        first.Hp = 0;
        spawner.DoDespawn(first);
        spawner.Despawn(first);
        if (reactivate)
            spawner.Activate();
        else
            spawner.DespawnAll();

        spawner.Update();
        var second = spawner.Created.Last();
        second.Hp = 0;
        spawner.DoDespawn(second);
        spawner.Despawn(second);
        _world.SpawnManager.RespawnObject(second);
        spawner.Update();

        await Assert.That(spawner.Created.Count).IsEqualTo(3);
        await Assert.That(Pending("Respawns").Single()).IsSameReferenceAs(first);
    }

    [Test]
    public async Task ClonedSpawners_KeepPendingRespawnOwnershipIndependent()
    {
        var template = CreateSpawner();
        var first = NpcSpawner.Clone(template);
        var second = NpcSpawner.Clone(template);
        // Isolate the existing placement collections to exercise pending-respawn ownership alone.
        first.Created = [];
        first.SpawnedNpcs = new();
        second.Created = [];
        second.SpawnedNpcs = new();
        SetHour(1, 4);
        first.Update();
        second.Update();
        var firstNpc = first.Created.Single();
        var secondNpc = second.Created.Single();
        firstNpc.Hp = 0;
        secondNpc.Hp = 0;
        first.DoDespawn(firstNpc);
        second.DoDespawn(secondNpc);
        first.Despawn(firstNpc);
        second.Despawn(secondNpc);

        SetHour(1, 6);
        first.Update();
        SetHour(2, 4);
        _world.SpawnManager.RespawnObject(secondNpc);
        second.Update();

        await Assert.That(second.Created.Count).IsEqualTo(2);
        await Assert.That(first.Created).HasSingleItem();
        await Assert.That(Pending("Respawns").Single()).IsSameReferenceAs(firstNpc);
    }

    [Test]
    public async Task RetiredOccurrence_LateLifetimeAndRemovalCallbacksCannotReleaseAReusedId()
    {
        var spawner = CreateSpawner();
        SetHour(1, 4);
        spawner.Update();
        var npc = spawner.Created.Single();
        var lifetimeTask = new NpcSpawnerDoDespawnTask(npc);

        SetHour(1, 6);
        spawner.Update();
        _world.SpawnManager.DespawnObject(npc);
        var reusedId = ObjectIdManager.Instance.GetNextId();
        await Assert.That(reusedId).IsEqualTo(npc.ObjId);

        lifetimeTask.Execute();
        spawner.DoDespawns([npc]);
        _world.SpawnManager.DespawnObject(npc);
        SetHour(2, 4);
        lifetimeTask.Execute();
        spawner.DespawnWithRespawn(npc);
        spawner.DoDespawnsNow([npc]);

        await Assert.That(Pending("Despawns")).IsEmpty();
        await Assert.That(Pending("Respawns")).IsEmpty();
        await Assert.That(npc.DeleteCalls).IsEqualTo(1);
        await Assert.That(ObjectIdManager.Instance.GetNextId()).IsNotEqualTo(reusedId);
    }

    [Test]
    public async Task DirectRemoval_BeforeQueuedRemoval_ReleasesTheIdExactlyOnce()
    {
        var spawner = CreateSpawner();
        SetHour(1, 4);
        spawner.Update();
        var npc = spawner.Created.Single();
        npc.Hp = 0;
        spawner.DoDespawn(npc);
        spawner.Despawn(npc);
        var reusedId = ObjectIdManager.Instance.GetNextId();
        await Assert.That(reusedId).IsEqualTo(npc.ObjId);

        _world.SpawnManager.DespawnObject(npc);
        spawner.Despawn(npc);

        await Assert.That(npc.DeleteCalls).IsEqualTo(1);
        await Assert.That(Pending("Despawns")).IsEmpty();
        await Assert.That(ObjectIdManager.Instance.GetNextId()).IsNotEqualTo(reusedId);
    }

    [Test]
    [Arguments(4, 6, 4, true)]
    [Arguments(4, 6, 6, false)]
    [Arguments(22, 2, 22, true)]
    [Arguments(22, 2, 1, true)]
    [Arguments(22, 2, 2, false)]
    [Arguments(22, 2, 12, false)]
    [Arguments(4, 4, 12, true)]
    public async Task OrdinaryClockWindow_UsesInclusiveStartAndExclusiveEnd(int start, int end, int current, bool active)
    {
        await Assert.That(NpcSpawner.IsTimeBetween(TimeSpan.FromHours(current),
            TimeSpan.FromHours(start), TimeSpan.FromHours(end))).IsEqualTo(active);
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances[field] = field.GetValue(null);
        field.SetValue(null, instance);
    }

    private void SetSchedules(params GameSchedules[] schedules)
    {
        _schedules.LoadGameSchedules(schedules.ToDictionary(s => s.Id));
        _schedules.LoadGameScheduleSpawners(schedules.ToDictionary(s => s.Id,
            s => new GameScheduleSpawners { Id = s.Id, GameScheduleId = s.Id, SpawnerId = (int)SpawnerId }));
    }

    private void SetHour(int day, int hour)
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, day, hour, 0, 0, TimeSpan.Zero));
    }

    private HashSet<GameObject> Pending(string property)
    {
        return (HashSet<GameObject>)typeof(SpawnManager)
            .GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_world.SpawnManager)!;
    }

    private RecordingNpcSpawner CreateSpawner()
    {
        var definition = new NpcSpawnerNpc(SpawnerId, TemplateId);
        return new RecordingNpcSpawner
        {
            SpawnerId = SpawnerId, UnitId = TemplateId, ParentWorld = _world,
            Position = new WorldSpawnPosition(), RespawnTime = 60, DespawnTime = 5,
            Template = new NpcSpawnerTemplate
            {
                Id = SpawnerId, ActivationState = true, MaxPopulation = 1,
                TestRadiusNpc = 0, TestRadiusPc = 1, Npcs = [definition]
            },
            SpawnableNpcs = [definition], NpcSpawnerIds = [SpawnerId]
        };
    }

    private sealed class RecordingNpcSpawner : NpcSpawner
    {
        public List<RecordingNpc> Created { get; set; } = [];

        public RecordingNpc SeedOccurrence(bool inCombat)
        {
            var npc = CreateOccurrence();
            npc.IsInBattle = inCombat;
            SpawnedNpcs[SpawnerId] = [npc];
            return npc;
        }

        protected override List<Npc> SpawnNpcDefinition(NpcSpawnerNpc definition) => [CreateOccurrence()];

        private RecordingNpc CreateOccurrence()
        {
            var npc = new RecordingNpc
            {
                ObjId = ObjectIdManager.Instance.GetNextId(), TemplateId = UnitId,
                ParentWorld = ParentWorld, Spawner = this, Hp = 100
            };
            Created.Add(npc);
            return npc;
        }
    }

    private sealed class RecordingNpc : Npc
    {
        public int DeleteCalls { get; private set; }
        public override void Delete() => DeleteCalls++;
    }
}
