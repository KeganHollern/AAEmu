using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;

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
        public override void Delete() => DeleteCalls++;
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
