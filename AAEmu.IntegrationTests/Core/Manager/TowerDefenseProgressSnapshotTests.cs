using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.TowerDefense;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Tasks.TowerDefense;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;
using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection(nameof(TowerDefenseMySql))]
[Trait("Category", "GameMySql")]
public sealed class TowerDefenseProgressSnapshotTests : IDisposable
{
    private const string EventKey = "test.progress";
    private const uint DefinitionId = 830288;
    private readonly string _manifest = Path.GetTempFileName();
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private readonly ConcurrentDictionary<uint, WorldInstance> _worlds;
    private readonly Mock<ITaskManager> _tasks = new();
    private bool _acceptTask = true;
    private readonly List<TowerDefenseCallbackTask> _submissions = [];

    public TowerDefenseProgressSnapshotTests()
    {
        SetSingleton(typeof(Singleton<GameScheduleManager>), "s_instance", new GameScheduleManager(null, TimeProvider.System));
        var worldManager = new WorldManager(null, null, null, null, null);
        SetSingleton(typeof(Singleton<WorldManager>), "s_instance", worldManager);
        _worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worldManager)!;
        var ids = new ObjectIdManager();
        ids.Initialize();
        SetSingleton(typeof(ObjectIdManager), "_instance", ids);
        var def = new TowerDef { Id = DefinitionId, ForceEndTime = 3600, TimeOfDayDayInterval = 1 };
        // Each step has 3 infantry and 2 captains. One captain alone cannot finish the event.
        for (uint i = 1; i <= 2; i++)
        {
            def.Progs.Add(new TowerDefProg
            {
                Id = i, CondToNextTime = 120, CondCompByAnd = true,
                SpawnTargets = { new() { Id = i, SpawnTargetId = 35, SpawnTargetType = TowerDefTargetType.NpcSpawner, DespawnOnNextStep = true } },
                KillTargets = { new() { Id = i, KillTargetId = 8834, KillTargetType = TowerDefTargetType.Npc, KillCount = 3 } }
            });
        }
        ((IDictionary<uint, TowerDef>)TowerDefGameData.Instance.TowerDefs).Add(DefinitionId, def);
        File.WriteAllText(_manifest, """
            {"schemaVersion":1,"events":[{"key":"test.progress","towerDefId":830288,"trigger":{"type":"Manual"},
            "completionTarget":{"npcId":8850,"count":2},"restartPolicy":"RestartCurrentStep",
            "sites":[{"key":"island","spotId":1,"bindings":{"35":["island-a","island-b","island-c","island-d","island-e"]}},
                {"key":"coast","spotId":4,"bindings":{"35":["coast-a","coast-b","coast-c","coast-d","coast-e"]}}]}]}
            """);
        _tasks.Setup(tasks => tasks.Schedule(It.IsAny<GameTask>(), It.IsAny<TimeSpan?>(), null, 1))
            .Returns((GameTask task, TimeSpan? delay, TimeSpan? repeat, int count) =>
            {
                var callback = (TowerDefenseCallbackTask)task;
                if (callback.Kind == TowerDefenseTimerKind.ProgressSnapshot)
                {
                    Assert.Equal(TimeSpan.FromSeconds(5), delay);
                    _submissions.Add(callback);
                    return _acceptTask;
                }
                return true;
            });
        _tasks.Setup(tasks => tasks.Cancel(It.IsAny<GameTask>())).Returns((GameTask task) => task.Cancelled = true);
    }

    [Fact]
    public void KillBurst_SavesOnlyChangedObjectivesAndRetriesFailedWrite()
    {
        using var manager = CreateManager();
        var occurrence = Start(manager);
        var soldiers = Units(occurrence, 8834);
        var captains = Units(occurrence, 8850);
        Kill(occurrence, new Npc { ObjId = 80, TemplateId = 8834 });
        Assert.Empty(_submissions);
        _acceptTask = false;
        Kill(occurrence, soldiers[0]);
        Assert.DoesNotContain(occurrence.ScheduledTasks.OfType<TowerDefenseCallbackTask>(),
            task => task.Kind == TowerDefenseTimerKind.ProgressSnapshot);
        _acceptTask = true;
        Kill(occurrence, soldiers[1]);
        Kill(occurrence, soldiers[1]);
        Kill(occurrence, captains[0]);
        Kill(occurrence, captains[0]);
        Assert.Equal(2, _submissions.Count); // One refusal and one accepted callback for the burst.
        AssertCounts(0, 0);
        ExecuteSql("""
            CREATE TRIGGER reject_progress BEFORE UPDATE ON tower_def_occurrences
            FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='disposable snapshot failure'
            """);
        try
        {
            _submissions[1].Execute();
            Assert.Equal(3, _submissions.Count);
            AssertCounts(0, 0);
        }
        finally
        {
            ExecuteSql("DROP TRIGGER reject_progress");
        }
        _submissions[2].Execute();
        AssertCounts(2, 1);

        Kill(occurrence, soldiers[2]);
        var pending = _submissions[3];
        Kill(occurrence, captains[1]);
        Assert.Empty(manager.GetActiveOccurrences());
        Assert.True(pending.Cancelled);
        pending.Execute();
        AssertCounts(3, 2);
        Assert.Equal("terminal_objective_complete", ExecuteSql("SELECT terminal_reason FROM tower_def_occurrences WHERE event_key='test.progress'"));
    }

    [Fact]
    public void StepTimerAndShutdown_CancelPendingSnapshotsAndKeepTerminalCreditAcrossSteps()
    {
        using var manager = CreateManager();
        var occurrence = Start(manager);
        foreach (var soldier in Units(occurrence, 8834))
            Kill(occurrence, soldier);
        Kill(occurrence, Units(occurrence, 8850)[0]);
        var oldSnapshot = Assert.Single(_submissions);
        var stepTimer = Assert.Single(occurrence.ScheduledTasks.OfType<TowerDefenseCallbackTask>(), task => task.Kind == TowerDefenseTimerKind.StepTimer);
        stepTimer.Execute();
        Assert.True(oldSnapshot.Cancelled);
        Assert.Equal(1, occurrence.CurrentStepOrdinal);
        AssertCounts(0, 1, "2");
        var nextSoldiers = Units(occurrence, 8834);
        Kill(occurrence, nextSoldiers[0]);
        oldSnapshot.Execute(); // A callback already taken by a worker before cancellation.
        AssertCounts(0, 1, "2");
        Assert.Equal(2, _submissions.Count);
        manager.Dispose();
        Assert.True(_submissions[1].Cancelled);
        _submissions[1].Execute();
        AssertCounts(1, 1, "2");
        Assert.Empty(occurrence.World.GetAllNpcs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartCurrentStep_RespawnsTheWholeWaveWithTheOriginalSiteAndDeadline(bool legacySnapshot)
    {
        using var first = CreateManager();
        var old = Start(first);
        Kill(old, Units(old, 8834)[0]);
        Kill(old, Units(old, 8850)[0]);
        Assert.Single(_submissions).Execute();
        AssertCounts(1, 1);
        Kill(old, Units(old, 8834)[1]); // This kill is still inside the snapshot interval.
        Assert.Equal(2u, old.Objectives[1].Current);
        AssertCounts(1, 1);

        if (legacySnapshot)
            ExecuteSql("""
                UPDATE tower_def_occurrences SET objective_progress=JSON_EXTRACT(objective_progress, '$.Step')
                 WHERE event_key='test.progress'
                """);

        // A fresh world and manager recover directly from MySQL, before the old process can flush.
        using var restarted = CreateManager(minutesAfterStart: 10);
        var current = Assert.Single(restarted.GetActiveOccurrences());
        Assert.Equal(old.OccurrenceKey, current.OccurrenceKey);
        Assert.Equal("coast", current.Site.Key);
        Assert.Equal(old.HardDeadlineUtc, current.HardDeadlineUtc);
        Assert.Equal(old.CurrentStepOrdinal, current.CurrentStepOrdinal);
        Assert.True(current.Generation > old.Generation);
        AssertCounts(0, 0);
        _submissions[1].Execute(); // Late delivery from the old generation must leave the recovered row intact.
        AssertCounts(0, 0);
        Assert.Equal(3, Units(current, 8834).Length);
        Assert.Equal(2, Units(current, 8850).Length);
        var originalIds = old.World.GetAllNpcs().Select(npc => npc.ObjId).ToHashSet();
        Assert.All(current.World.GetAllNpcs(), npc =>
        {
            Assert.DoesNotContain(npc.ObjId, originalIds);
            Assert.Equal(current.Generation, npc.TowerDefenseSpawnToken.Generation);
            Assert.Equal("coast", npc.TowerDefenseSpawnToken.SiteKey);
        });
        Kill(current, Units(current, 8850)[0]);
        Assert.Single(restarted.GetActiveOccurrences());
        Kill(current, Units(current, 8850)[1]);
        Assert.Empty(restarted.GetActiveOccurrences());
    }

    private TowerDefenseManager CreateManager(int minutesAfterStart = 0)
    {
        var world = new WorldInstance(new WorldTemplate { Name = "main_world" }, 0, true, 1);
        _worlds[world.Id] = world;
        world.SpawnManager = new SpawnManager(world);
        foreach (var site in new[] { "island", "coast" })
        {
            foreach (var key in new[] { "a", "b", "c", "d", "e" })
                world.SpawnManager.AddNpcSpawner(new FixtureSpawner
                {
                    UnitId = key is "d" or "e" ? 8850u : 8834u, NpcSpawnerIds = [35u],
                    StartInactive = true, EventPlacementId = $"{site}-{key}", EventSiteKey = site
                });
        }
        var worlds = new Mock<IWorldManager>();
        worlds.Setup(value => value.GetWorlds()).Returns([world]);
        worlds.Setup(value => value.GetZoneId(world.Template, 0, 0)).Returns(7u);
        var zones = new Mock<IZoneManager>();
        zones.Setup(value => value.GetZoneByKey(7)).Returns(new Zone { GroupId = 2 });
        var options = Options.Create(new AppConfiguration { TowerDefense = new() { Enabled = true, ManifestPath = _manifest } });
        var manager = new TowerDefenseManager(Mock.Of<ITimeManager>(), worlds.Object, zones.Object, _tasks.Object,
            new FixedClock(minutesAfterStart), options);
        manager.Load();
        manager.Initialize();
        manager.OnWorldsInitialized();
        return manager;
    }

    private static TowerDefenseOccurrence Start(TowerDefenseManager manager)
    {
        Assert.True(manager.StartManual(EventKey, "coast", out var message), message);
        return Assert.Single(manager.GetActiveOccurrences());
    }

    private static Npc[] Units(TowerDefenseOccurrence occurrence, uint templateId) =>
        occurrence.World.GetAllNpcs().Where(npc => npc.TemplateId == templateId).OrderBy(npc => npc.ObjId).ToArray();

    private static void Kill(TowerDefenseOccurrence occurrence, Npc victim) =>
        occurrence.World.Events.OnUnitKilled(occurrence.World, new OnUnitKilledArgs { Victim = victim });

    private static void AssertCounts(uint step, uint terminal, string row = "1")
    {
        var data = JObject.Parse((string)ExecuteSql("SELECT objective_progress FROM tower_def_occurrences WHERE event_key='test.progress'"));
        Assert.Equal(step, (uint)data["Step"][row]["Current"]);
        Assert.Equal(terminal, (uint)data["Terminal"]["Current"]);
    }

    private static object ExecuteSql(string sql)
    {
        using var db = MySQL.CreateConnection();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void SetSingleton(Type owner, string name, object instance)
    {
        var field = owner.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, instance);
    }

    public void Dispose()
    {
        ((IDictionary<uint, TowerDef>)TowerDefGameData.Instance.TowerDefs).Remove(DefinitionId);
        foreach (var (field, previous) in _singletons)
            field.SetValue(null, previous);
        File.Delete(_manifest);
        ExecuteSql("DELETE FROM tower_def_occurrences WHERE event_key='test.progress'");
    }

    private sealed class FixedClock(int minutesAfterStart) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddYears(60).AddMinutes(minutesAfterStart);
    }

    // Leave AI and the full NPC template catalog outside this disposable world.
    private sealed class FixtureSpawner : NpcSpawner
    {
        protected override List<Npc> SpawnNpcDefinition(NpcSpawnerNpc definition)
        {
            var npc = new FixtureNpc { ObjId = ObjectIdManager.Instance.GetNextId(), TemplateId = definition.MemberId,
                Spawner = this, Hp = 100, ParentWorld = ParentWorld };
            ParentWorld.AddObject(npc);
            Assert.True(ParentWorld.EventSpawnOwnership.Register(npc, PendingTowerDefenseSpawnToken));
            return [npc];
        }
    }

    private sealed class FixtureNpc : Npc
    {
        public override void Delete() => ParentWorld.RemoveObject(this);
    }
}
