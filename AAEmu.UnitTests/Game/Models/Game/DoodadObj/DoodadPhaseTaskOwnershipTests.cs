using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Schedules;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class DoodadPhaseTaskOwnershipTests
{
    private const uint NextPhase = 200;
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private RecordingPhaseFunc _phaseEffect;

    [Before(Test)]
    public void SetUp()
    {
        _previousInstances.Clear();
        var doodads = new DoodadManager(Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IDoodadIdManager>().Object, Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        _phaseEffect = new RecordingPhaseFunc { Id = 1 };
        SetField(doodads, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>());
        SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [NextPhase] = [new DoodadPhaseFunc
            {
                GroupId = NextPhase, FuncId = _phaseEffect.Id, FuncType = nameof(RecordingPhaseFunc)
            }]
        });
        SetField(doodads, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(RecordingPhaseFunc)] = new() { [_phaseEffect.Id] = _phaseEffect }
        });
        ReplaceSingleton(doodads);
        ReplaceSingleton(new TaskManager(Mock.Of<ITickManager>().Object));
        ReplaceSingleton(new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));
        ReplaceSingleton(new AreaTriggerManager());
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(CallbackKind.Timer, false)]
    [Arguments(CallbackKind.Timer, true)]
    [Arguments(CallbackKind.Growth, false)]
    [Arguments(CallbackKind.Growth, true)]
    [Arguments(CallbackKind.TimeOfDay, false)]
    [Arguments(CallbackKind.TimeOfDay, true)]
    [Arguments(CallbackKind.Clout, false)]
    [Arguments(CallbackKind.Clout, true)]
    [Arguments(CallbackKind.Final, false)]
    [Arguments(CallbackKind.Final, true)]
    public async Task RetiredOrReplacedCallback_DoesNotRunEffectsOrClearReplacement(CallbackKind kind, bool replace)
    {
        var owner = CreateOwner();
        var task = CreateTask(kind, owner);
        owner.FuncTask = task;
        var replacement = replace ? new DoodadFuncGrowthTask(null, owner, 0, 0, 3) : null;
        owner.FuncTask = replacement;

        // The task runner may already hold this callback when cancellation removes it from the queue.
        task.Execute();

        await Assert.That(_phaseEffect.Calls).IsEqualTo(0);
        await Assert.That(owner.FuncGroupId).IsEqualTo(0u);
        await Assert.That(owner.DeleteCalls).IsEqualTo(0);
        await Assert.That(owner.FuncTask).IsSameReferenceAs(replacement);
    }

    [Test]
    [Arguments(CallbackKind.Timer)]
    [Arguments(CallbackKind.Growth)]
    [Arguments(CallbackKind.TimeOfDay)]
    [Arguments(CallbackKind.Clout)]
    public async Task CurrentCallback_RunsPhaseEffectOnce(CallbackKind kind)
    {
        var owner = CreateOwner();
        var task = CreateTask(kind, owner);
        owner.FuncTask = task;

        task.Execute();
        task.Execute();

        await Assert.That(_phaseEffect.Calls).IsEqualTo(1);
        await Assert.That(owner.FuncGroupId).IsEqualTo(NextPhase);
        await Assert.That(owner.FuncTask).IsNull();
    }

    [Test]
    public async Task Clout_OriginSourceDiffersFromTaskOwner_PreservesTheOriginTask()
    {
        var phaseOwner = CreateOwner();
        var origin = CreateOwner();
        var originTask = new DoodadFuncTimerTask(null, origin, 0, 0);
        origin.FuncTask = originTask;
        var task = new DoodadFuncCloutTask(null, origin, 0, 0, new AreaTrigger(), phaseOwner);
        phaseOwner.FuncTask = task;

        task.Execute();

        await Assert.That(_phaseEffect.Calls).IsEqualTo(0);
        await Assert.That(phaseOwner.FuncTask).IsNull();
        await Assert.That(origin.FuncTask).IsSameReferenceAs(originTask);
    }

    [Test]
    public async Task Final_WithoutRespawn_DeletesOnce()
    {
        var owner = CreateOwner();
        var task = new DoodadFuncFinalTask(null, owner, 0, false, 0);
        owner.FuncTask = task;

        task.Execute();
        task.Execute();

        await Assert.That(owner.DeleteCalls).IsEqualTo(1);
        await Assert.That(owner.FuncTask).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScheduledExpiry_CloutOnDistinctOrigin_CleansTriggerEvenAfterDispatch(bool alreadyDispatched)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 4, 0, 0, TimeSpan.Zero));
        var schedules = new GameScheduleManager(Mock.Of<IGameDataManager>().Object, clock);
        schedules.LoadGameSchedules(new()
        {
            [1] = new GameSchedules
            {
                Id = 1, StYear = 2030, StMonth = 1, StDay = 1, StHour = 4,
                EdYear = 2030, EdMonth = 1, EdDay = 1, EdHour = 6
            }
        });
        schedules.LoadGameScheduleDoodads(new()
        {
            [1] = new GameScheduleDoodads { Id = 1, GameScheduleId = 1, DoodadId = 1 }
        });
        ReplaceSingleton(schedules);
        var objectIdField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances[objectIdField] = objectIdField.GetValue(null);
        var objectIds = new ObjectIdManager();
        objectIds.Initialize();
        objectIdField.SetValue(null, objectIds);

        var phaseOwner = CreateOwner();
        phaseOwner.ObjId = objectIds.GetNextId();
        var spawner = new DoodadSpawner { UnitId = 1, Last = phaseOwner };
        spawner._spawned.Add(phaseOwner);
        phaseOwner.Spawner = spawner;
        var origin = CreateOwner();
        origin.IsVisible = true;
        var originTask = new DoodadFuncTimerTask(null, origin, 0, 0);
        origin.FuncTask = originTask;
        var trigger = new AreaTrigger { Owner = origin };
        AreaTriggerManager.Instance.AddAreaTrigger(trigger);
        var phaseTask = new DoodadFuncCloutTask(null, origin, 0, (int)NextPhase, trigger, phaseOwner);
        phaseOwner.FuncTask = phaseTask;
        TaskManager.Instance.Schedule(phaseTask, TimeSpan.FromHours(4));
        if (alreadyDispatched)
            TaskManager.Instance.Cancel(phaseTask); // Simulate the runner already holding the callback.

        clock.Advance(TimeSpan.FromHours(2));
        spawner.DoDespawn(phaseOwner);
        phaseTask.Execute();
        phaseTask.Retire();

        var removals = (List<AreaTrigger>)typeof(AreaTriggerManager)
            .GetField("_removeQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(AreaTriggerManager.Instance)!;
        await Assert.That(removals).HasSingleItem();
        await Assert.That(removals[0]).IsSameReferenceAs(trigger);
        await Assert.That(origin.AttachAreaTriggers).IsEmpty();
        await Assert.That(origin.IsVisible).IsTrue();
        await Assert.That(origin.FuncTask).IsSameReferenceAs(originTask);
        await Assert.That(origin.FuncGroupId).IsEqualTo(0u);
        await Assert.That(origin.DeleteCalls).IsEqualTo(0);
        await Assert.That(phaseOwner.DeleteCalls).IsEqualTo(1);
        await Assert.That(phaseOwner.FuncTask).IsNull();
        await Assert.That(_phaseEffect.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task CurrentClout_FinalPhase_DetachesTriggerBeforeDeletingOrigin()
    {
        var owner = CreateOwner();
        var trigger = new AreaTrigger { Owner = owner };
        AreaTriggerManager.Instance.AddAreaTrigger(trigger);
        owner.BeforeDelete = () =>
        {
            if (owner.AttachAreaTriggers.Contains(trigger))
                throw new InvalidOperationException("Doodad deletion would clean the same trigger a second time.");
        };
        var task = new DoodadFuncCloutTask(null, owner, 0, -1, trigger, owner);
        owner.FuncTask = task;

        task.Execute();
        task.Retire();

        var removals = (List<AreaTrigger>)typeof(AreaTriggerManager)
            .GetField("_removeQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(AreaTriggerManager.Instance)!;
        await Assert.That(removals).HasSingleItem();
        await Assert.That(owner.DeleteCalls).IsEqualTo(1);
        await Assert.That(owner.FuncTask).IsNull();
    }

    [Test]
    [Arguments(1218u, 1877u, 300_000)]
    [Arguments(1219u, 1878u, 600_000)]
    [Arguments(1658u, 3045u, 600_000)]
    public async Task NaviRemoval_ResumesSavedDeadlineAndExpiresOnce(uint template, uint phase, int after)
    {
        var owner = new RecordingDoodad { TemplateId = template, Template = new DoodadTemplate { Id = template } };
        BindPhaseFunctions((phase, new DoodadFuncNaviRemoveTimer { After = after }));

        owner.DoChangePhase(null, (int)phase);
        var start = owner.PhaseTime;
        await Assert.That(Math.Abs((owner.FuncTask.TriggerTime - start).TotalMilliseconds - after) < 1000).IsTrue();

        // SpawnManager restores phase_time and OverridePhaseTime from the same saved column.
        var restored = new RecordingDoodad { TemplateId = template, Template = owner.Template, FuncGroupId = phase };
        restored.PhaseTime = restored.OverridePhaseTime = start.AddMilliseconds(-after + 30_000);
        var deadline = restored.PhaseTime.AddMilliseconds(after);
        restored.DoChangePhase(null, (int)phase);
        await Assert.That(Math.Abs((restored.FuncTask.TriggerTime - deadline).TotalMilliseconds) < 1000).IsTrue();
        await Assert.That(restored.OverridePhaseTime).IsEqualTo(DateTime.MinValue);
        await Assert.That(restored.PhaseTime).IsEqualTo(deadline.AddMilliseconds(-after));

        // A repeated settle within the same phase cannot grant another full timer.
        var superseded = restored.FuncTask;
        restored.DoChangePhase(null, (int)phase);
        superseded.Execute();
        await Assert.That(restored.DeleteCalls).IsEqualTo(0);
        await Assert.That(Math.Abs((restored.FuncTask.TriggerTime - deadline).TotalMilliseconds) < 1000).IsTrue();

        restored.OverridePhaseTime = start.AddMilliseconds(-after - 60_000);
        var before = DateTime.UtcNow;
        restored.DoChangePhase(null, (int)phase);
        var expired = restored.FuncTask;
        await Assert.That(expired.TriggerTime >= before && expired.TriggerTime <= DateTime.UtcNow).IsTrue();
        expired.Execute();
        expired.Execute();
        await Assert.That(restored.DeleteCalls).IsEqualTo(1);
    }

    [Test]
    public async Task NaviGate_PortalPhasesOwnTheirTimersUntilTheGateCloses()
    {
        BindPhaseFunctions(
            (1878u, new DoodadFuncNaviRemoveTimer { After = 600_000 }),
            (2046u, new DoodadFuncTimer { NextPhase = 2056, Delay = 2_000 }),
            (2056u, new DoodadFuncTimer { NextPhase = 2057, Delay = 600_000 }),
            (2057u, new DoodadFuncTimer { NextPhase = 1878, Delay = 2_000 }));
        var gate = new RecordingDoodad { TemplateId = 1219, Template = new DoodadTemplate { Id = 1219 } };
        gate.DoChangePhase(null, 1878);
        var oldRemoval = gate.FuncTask;
        gate.DoChangePhase(null, 2046);
        oldRemoval.Execute();
        await Assert.That(gate.DeleteCalls).IsEqualTo(0);
        gate.FuncTask.Execute();
        await Assert.That(gate.FuncGroupId).IsEqualTo(2056u);
        gate.FuncTask.Execute();
        await Assert.That(gate.FuncGroupId).IsEqualTo(2057u);
        gate.FuncTask.Execute();
        await Assert.That(gate.FuncGroupId).IsEqualTo(1878u);
        var newRemoval = gate.FuncTask;
        await Assert.That(Math.Abs((newRemoval.TriggerTime - gate.PhaseTime).TotalMilliseconds - 600_000) < 1000).IsTrue();
        oldRemoval.Execute();
        await Assert.That(gate.DeleteCalls).IsEqualTo(0);
        newRemoval.Execute();
        await Assert.That(gate.DeleteCalls).IsEqualTo(1);
    }

    [Test]
    public async Task NaviRemoval_AfterEarlyDeletion_CannotDeleteAReplacement()
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        SetField(WorldManager.Instance, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            new Dictionary<uint, WorldInstance> { [world.Id] = world }));
        var original = new Doodad { ObjId = 2001, ParentWorld = world };
        var replacement = new Doodad { ObjId = original.ObjId, ParentWorld = world };
        world.AddObject(original);
        new DoodadFuncNaviRemoveTimer { After = 300_000 }.Use(null, original);
        var callback = original.FuncTask;
        original.Delete();
        world.AddObject(replacement);
        callback.Execute();
        await Assert.That(world.GetDoodad(original.ObjId)).IsSameReferenceAs(replacement);
    }

    [Test]
    public async Task NaviRemoval_ReviewedCompactSelectsTheThreeTimersAndThePortalCycle()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 server compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.doodad_almighty_id, it.item_id, p.doodad_func_group_id, n.after
            FROM doodad_phase_funcs p
            JOIN doodad_func_groups g ON g.id = p.doodad_func_group_id
            JOIN doodad_func_navi_remove_timers n ON n.id = p.actual_func_id
            JOIN item_spawn_doodads it ON it.doodad_id = g.doodad_almighty_id
            WHERE p.actual_func_type = 'DoodadFuncNaviRemoveTimer' ORDER BY 1
            """;
        var rows = new List<(int, int, int, int)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        await Assert.That(rows.SequenceEqual([(1218, 8120, 1877, 300000), (1219, 8122, 1878, 600000),
            (1658, 14587, 3045, 600000)])).IsTrue();

        command.CommandText = """
            SELECT p.doodad_func_group_id, t.delay, t.next_phase FROM doodad_phase_funcs p
            JOIN doodad_func_groups g ON g.id = p.doodad_func_group_id
            JOIN doodad_func_timers t ON t.id = p.actual_func_id
            WHERE g.doodad_almighty_id = 1219 AND p.actual_func_type = 'DoodadFuncTimer' ORDER BY 1
            """;
        var phases = new List<(int, int, int)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                phases.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        await Assert.That(phases.SequenceEqual([(2046, 2000, 2056), (2056, 600000, 2057), (2057, 2000, 1878)])).IsTrue();

        command.CommandText = """
            SELECT next_phase FROM doodad_funcs WHERE doodad_func_group_id = 1878
            AND actual_func_type = 'DoodadFuncNaviOpenPortal'
            """;
        await Assert.That(Convert.ToInt32(command.ExecuteScalar())).IsEqualTo(2046);
    }

    private static void BindPhaseFunctions(params (uint Group, DoodadPhaseFuncTemplate Function)[] bindings)
    {
        var phases = new Dictionary<uint, List<DoodadPhaseFunc>>();
        var functions = new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>();
        foreach (var (group, function) in bindings)
        {
            var type = function.GetType().Name;
            function.Id = group;
            phases[group] = [new DoodadPhaseFunc { GroupId = group, FuncId = function.Id, FuncType = type }];
            if (!functions.TryGetValue(type, out var templates))
                functions[type] = templates = [];
            templates[function.Id] = function;
        }
        SetField(DoodadManager.Instance, "_phaseFuncs", phases);
        SetField(DoodadManager.Instance, "_phaseFuncTemplates", functions);
    }

    private static RecordingDoodad CreateOwner()
    {
        return new RecordingDoodad { TemplateId = 1, Template = new DoodadTemplate { Id = 1 } };
    }

    private static DoodadFuncTask CreateTask(CallbackKind kind, Doodad owner)
    {
        return kind switch
        {
            CallbackKind.Timer => new DoodadFuncTimerTask(null, owner, 0, (int)NextPhase),
            CallbackKind.Growth => new DoodadFuncGrowthTask(null, owner, 0, (int)NextPhase, 2),
            CallbackKind.TimeOfDay => new DoodadFuncTodTask(null, owner, 0, (int)NextPhase),
            CallbackKind.Clout => new DoodadFuncCloutTask(null, owner, 0, (int)NextPhase, new AreaTrigger(), owner),
            CallbackKind.Final => new DoodadFuncFinalTask(null, owner, 0, false, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private void ReplaceSingleton<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances[field] = field.GetValue(null);
        field.SetValue(null, instance);
    }

    private static void SetField(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    }

    public enum CallbackKind { Timer, Growth, TimeOfDay, Clout, Final }

    private sealed class RecordingDoodad : Doodad
    {
        public int DeleteCalls { get; private set; }
        public Action BeforeDelete { get; set; }
        public override void Delete()
        {
            BeforeDelete?.Invoke();
            DeleteCalls++;
        }
    }

    private sealed class RecordingPhaseFunc : DoodadPhaseFuncTemplate
    {
        public int Calls { get; private set; }
        public override bool Use(BaseUnit caster, Doodad owner)
        {
            Calls++;
            return false;
        }
    }
}
