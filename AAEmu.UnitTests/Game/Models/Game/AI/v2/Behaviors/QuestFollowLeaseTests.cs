using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.AI.v2.Params;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Behaviors;

[NotInParallel]
public sealed class QuestFollowLeaseTests
{
    private readonly Dictionary<FieldInfo, object> _previousSingletons = [];
    private QuestInteractionTestModels _models;
    private WorldInstance _world;
    private CharacterMock _target;
    private ProbeNpc _npc;
    private ProbeAi _ai;
    private object _claim;
    private int _lost;

    [Before(Test)]
    public void SetUp()
    {
        _models = new QuestInteractionTestModels();
        var worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(worlds);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        _target = new CharacterMock { Id = 7, ObjId = 70, Hp = 100 };
        SetField(_target, "_parentWorld", _world, typeof(GameObject));
        SetField(_target, "<IsOnline>k__BackingField", true, typeof(Character));
        _target.Transform.Local.Position = new Vector3(10, 0, 0);
        _npc = new ProbeNpc
        {
            ObjId = 81, TemplateId = 5055, Hp = 100, DisabledSetPosition = true,
            Template = new NpcTemplate { Id = 5055, Scale = 1, ModelId = 1 }
        };
        SetField(_npc, "_parentWorld", _world, typeof(GameObject));
        _ai = new ProbeAi { Owner = _npc, IdlePosition = new Vector3(3, 4, 5), _nextAlertCheckTime = DateTime.MaxValue };
        _npc.Ai = _ai;
        _ai.Start();
        _ai.GoToIdle();
        _claim = new object();
        _lost = 0;
    }

    [After(Test)]
    public void TearDown()
    {
        _ai.StopQuestFollow(_claim);
        _models.Dispose();
        foreach (var (field, previous) in _previousSingletons)
            field.SetValue(null, previous);
        _previousSingletons.Clear();
    }

    [Test]
    public async Task Start_RepeatedSameClaim_IsIdempotentAndExclusive()
    {
        var beforeHooks = _npc.Events.OnDeath.GetInvocationList().Length;
        await Assert.That(Start()).IsTrue();
        await Assert.That(Start()).IsTrue();
        await Assert.That(_ai.TryStartQuestFollow(new object(), _target, () => { })).IsFalse();
        await Assert.That(_ai.FollowRequests).IsEqualTo(1);
        await Assert.That(_npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(beforeHooks + 1);
        await Assert.That(_ai.AiFollowUnitObj).IsSameReferenceAs(_target);
    }

    [Test]
    [Arguments("current_command")]
    [Arguments("queued_command")]
    [Arguments("normal_follow")]
    [Arguments("patrol")]
    public async Task Start_UnrelatedControl_RejectsClaimWithoutChangingControl(string control)
    {
        var other = new Npc { Hp = 100 };
        var command = new AiCommands { CmdId = AiCommandCategory.Timeout, Param1 = 5 };
        switch (control)
        {
            case "current_command": _ai.AiCurrentCommand = command; break;
            case "queued_command": _ai.AiCommandsQueue.Enqueue(command); break;
            case "normal_follow": _ai.AiFollowUnitObj = other; break;
            case "patrol": _npc.IsInPatrol = true; break;
        }

        await Assert.That(Start()).IsFalse();
        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsFalse();
        if (control == "current_command")
            await Assert.That(_ai.AiCurrentCommand).IsSameReferenceAs(command);
        else if (control == "queued_command")
            await Assert.That(_ai.AiCommandsQueue.Peek()).IsSameReferenceAs(command);
        else if (control == "normal_follow")
            await Assert.That(_ai.AiFollowUnitObj).IsSameReferenceAs(other);
        else
            await Assert.That(_npc.IsInPatrol).IsTrue();
    }

    [Test]
    public async Task Stop_WrongOwner_DoesNotClearClaim()
    {
        Start();
        _ai.StopQuestFollow(new object());

        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsTrue();
        await Assert.That(_ai.AiFollowUnitObj).IsSameReferenceAs(_target);
    }

    [Test]
    public async Task Stop_OwnClaim_RestoresIdlePositionAndKeepsPaths()
    {
        var path = new AiPathPoint { Position = new Vector3(100, 20, 0) };
        var originalIdle = _ai.IdlePosition;
        _ai.PathHandler.AiPathPoints.Add(path);
        _ai.PathHandler.AiPathPointsRemaining.Enqueue(path);
        Start();
        _ai.IdlePosition = new Vector3(8, 9, 0);
        _ai.StopQuestFollow(_claim);

        await Assert.That(_ai.AiFollowUnitObj).IsNull();
        await Assert.That(_ai.IdlePosition).IsEqualTo(originalIdle);
        await Assert.That(_ai.PathHandler.AiPathPoints.Single()).IsSameReferenceAs(path);
        await Assert.That(_ai.PathHandler.AiPathPointsRemaining.Peek()).IsSameReferenceAs(path);
        await Assert.That(_npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
        await Assert.That(_lost).IsEqualTo(0);
    }

    [Test]
    [Arguments("combat")]
    [Arguments("dead")]
    [Arguments("despawn")]
    public async Task Stop_DifferentCurrentBehavior_PreservesBehavior(string state)
    {
        Start();
        switch (state)
        {
            case "combat": _ai.GoToCombat(); break;
            case "dead": _ai.GoToDead(); break;
            case "despawn": _ai.GoToDespawn(); break;
        }
        var behavior = _ai.GetCurrentBehavior();
        _ai.StopQuestFollow(_claim);

        await Assert.That(_ai.GetCurrentBehavior()).IsSameReferenceAs(behavior);
        await Assert.That(_ai.AiFollowUnitObj).IsNull();
    }

    [Test]
    public async Task Tick_ExternalFollowReplacement_PreservesNewTargetAndIdlePosition()
    {
        Start();
        var other = new Npc { ObjId = 82, Hp = 100, Template = new NpcTemplate { Scale = 1 } };
        SetField(other, "_parentWorld", _world, typeof(GameObject));
        _ai.AiFollowUnitObj = other;
        _ai.IdlePosition = new Vector3(100, 20, 0);
        _ai.GoToCombat();
        var behavior = _ai.GetCurrentBehavior();
        _ai.Tick(TimeSpan.Zero);
        _ai.StopQuestFollow(_claim);

        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsFalse();
        await Assert.That(_ai.AiFollowUnitObj).IsSameReferenceAs(other);
        await Assert.That(_ai.IdlePosition).IsEqualTo(new Vector3(100, 20, 0));
        await Assert.That(_ai.GetCurrentBehavior()).IsSameReferenceAs(behavior);
        await Assert.That(_lost).IsEqualTo(1);
    }

    [Test]
    [Arguments("queued_command")]
    [Arguments("current_command")]
    [Arguments("command_behavior")]
    [Arguments("path_behavior")]
    [Arguments("dummy_behavior")]
    [Arguments("patrol")]
    public async Task Replacement_NewControl_ReleasesLeaseWithoutChangingNewControl(string replacement)
    {
        Start();
        var command = new AiCommands { CmdId = AiCommandCategory.Timeout, Param1 = 5 };
        var newIdle = new Vector3(100, 20, 0);
        _ai.IdlePosition = newIdle;
        var simulation = new Simulation(_npc) { MoveFileName = "normal_patrol", MoveToPathEnabled = true };
        switch (replacement)
        {
            case "queued_command": _ai.AiCommandsQueue.Enqueue(command); break;
            case "current_command": _ai.AiCurrentCommand = command; break;
            case "command_behavior": _ai.EnqueueAiCommands(new[] { command }); break;
            case "path_behavior": _ai.GoToFollowPath(); break;
            case "dummy_behavior": _ai.GoToDummy(); break;
            case "patrol": _npc.Simulation = simulation; _npc.IsInPatrol = true; break;
        }
        var behavior = _ai.GetCurrentBehavior();
        _ai.Tick(TimeSpan.Zero);
        _ai.StopQuestFollow(_claim);

        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsFalse();
        await Assert.That(_ai.AiFollowUnitObj).IsNull();
        if (replacement == "current_command")
            await Assert.That(_ai.AiCurrentCommand).IsSameReferenceAs(command);
        if (replacement is "queued_command" or "command_behavior")
            await Assert.That(_ai.AiCommandsQueue.Peek()).IsSameReferenceAs(command);
        if (replacement is "queued_command" or "current_command" or "command_behavior" or "patrol")
            await Assert.That(_ai.IdlePosition).IsEqualTo(newIdle);
        if (replacement == "patrol")
        {
            await Assert.That(_npc.Simulation).IsSameReferenceAs(simulation);
            await Assert.That(_npc.IsInPatrol).IsTrue();
            await Assert.That(_npc.Simulation.MoveToPathEnabled).IsTrue();
            await Assert.That(_npc.Simulation.MoveFileName).IsEqualTo("normal_patrol");
        }
        if (replacement is "command_behavior" or "path_behavior" or "dummy_behavior")
            await Assert.That(_ai.GetCurrentBehavior()).IsSameReferenceAs(behavior);
        await Assert.That(_lost).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalIdleAfterCombat_ResumesOwnFollow(bool useDefault)
    {
        Start();
        _ai.GoToCombat();
        if (useDefault)
            _ai.GoToDefaultBehavior();
        else
            _ai.GoToIdle();

        await Assert.That(_ai.GetCurrentBehavior()).IsTypeOf<FollowUnitBehavior>();
        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsTrue();
        await Assert.That(_ai.AiFollowUnitObj).IsSameReferenceAs(_target);
    }

    [Test]
    [Arguments("path")]
    [Arguments("dummy")]
    [Arguments("combat")]
    [Arguments("command")]
    public async Task ReleasedLease_NewControlBeforeNextTick_DoesNotRestoreOldIdle(string control)
    {
        Start();
        _ai.StopQuestFollow(_claim);
        switch (control)
        {
            case "path": _ai.GoToFollowPath(); break;
            case "dummy": _ai.GoToDummy(); break;
            case "combat": _ai.GoToCombat(); break;
            case "command": _ai.EnqueueAiCommands(new[] { new AiCommands { CmdId = AiCommandCategory.Timeout, Param1 = 5 } }); break;
        }
        var behavior = _ai.GetCurrentBehavior();

        _ai.Tick(TimeSpan.Zero);

        await Assert.That(_ai.GetCurrentBehavior()).IsSameReferenceAs(behavior);
        await Assert.That(_ai.AiFollowUnitObj).IsNull();
        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsFalse();
    }

    [Test]
    public async Task Stop_OwnClaim_DefersIdleToNextOwningTickWithoutRegionActivity()
    {
        Start();
        var idleRequests = _ai.IdleRequests;
        _ai.StopQuestFollow(_claim);

        await Assert.That(_ai.IdleRequests).IsEqualTo(idleRequests);
        await Assert.That(_ai.GetCurrentBehavior()).IsTypeOf<FollowUnitBehavior>();
        _ai.Tick(TimeSpan.Zero);
        await Assert.That(_ai.IdleRequests).IsEqualTo(idleRequests + 1);
        await Assert.That(_ai.GetCurrentBehavior()).IsSameReferenceAs(_ai.GetAiBehaviorList()[BehaviorKind.Idle]);
    }

    [Test]
    public async Task ReleasedLease_NewClaimBeforeNextTick_KeepsNewClaim()
    {
        Start();
        _ai.StopQuestFollow(_claim);
        var newClaim = new object();
        var idleRequests = _ai.IdleRequests;
        var accepted = _ai.TryStartQuestFollow(newClaim, _target, () => { });

        _ai.Tick(TimeSpan.Zero);

        await Assert.That(accepted).IsTrue();
        await Assert.That(_ai.IdleRequests).IsEqualTo(idleRequests);
        await Assert.That(_ai.IsQuestFollowOwner(newClaim)).IsTrue();
        await Assert.That(_ai.AiFollowUnitObj).IsSameReferenceAs(_target);
        _ai.StopQuestFollow(newClaim);
    }

    [Test]
    [Arguments("offline")]
    [Arguments("dead")]
    [Arguments("other_world")]
    [Arguments("other_instance")]
    [Arguments("ai_replaced")]
    public async Task Tick_InvalidTargetOrAi_ReleasesLeaseAndCallsLossOnce(string state)
    {
        Start();
        switch (state)
        {
            case "offline": SetField(_target, "<IsOnline>k__BackingField", false, typeof(Character)); break;
            case "dead": _target.Hp = 0; break;
            case "other_world":
                SetField(_target, "_parentWorld", new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 2), typeof(GameObject));
                break;
            case "other_instance": SetField(_target.Transform, "_instanceId", 9U); break;
            case "ai_replaced": _npc.Ai = new ProbeAi { Owner = _npc }; _npc.CurrentTarget = _target; break;
        }
        _ai.Tick(TimeSpan.Zero);
        _ai.Tick(TimeSpan.Zero);

        await Assert.That(_ai.IsQuestFollowOwner(_claim)).IsFalse();
        await Assert.That(_ai.AiFollowUnitObj).IsNull();
        await Assert.That(_lost).IsEqualTo(1);
        await Assert.That(_npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
        if (state == "ai_replaced")
            await Assert.That(_npc.CurrentTarget).IsSameReferenceAs(_target);
    }

    [Test]
    [Arguments(1000, 5f)]
    [Arguments(1100, 5.5f)]
    public async Task Tick_FollowMovement_UsesFullElapsedSeconds(int milliseconds, float expectedX)
    {
        Start();

        _ai.Tick(TimeSpan.FromMilliseconds(milliseconds));

        await Assert.That(_npc.Transform.World.Position.X).IsEqualTo(expectedX);
        await Assert.That(_npc.Movements.Count).IsEqualTo(1);
        await Assert.That(_npc.Movements[0].X).IsEqualTo(expectedX);
        await Assert.That(_ai.IdlePosition).IsEqualTo(_npc.Transform.World.Position);
    }

    private bool Start() => _ai.TryStartQuestFollow(_claim, _target, () => _lost++);

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSingletons.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value, Type declaringType = null) =>
        (declaringType ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ProbeNpc : MovementProbeNpc
    {
        public override float BaseMoveSpeed => 1;
    }

    private sealed class ProbeAi : NpcAi
    {
        public int FollowRequests { get; private set; }
        public int IdleRequests { get; private set; }

        protected override void Build()
        {
            AddBehavior(BehaviorKind.FollowUnit, new FollowUnitBehavior());
            foreach (var kind in new[] { BehaviorKind.Idle, BehaviorKind.Attack, BehaviorKind.Dead,
                BehaviorKind.Despawning, BehaviorKind.FollowPath, BehaviorKind.RunCommandSet, BehaviorKind.Dummy })
                AddBehavior(kind, new DoNothingBehavior());
            AddBehavior(BehaviorKind.DoNothing, new DoNothingBehavior()).SetDefaultBehavior();
        }

        public override void GoToFollowUnit()
        {
            FollowRequests++;
            base.GoToFollowUnit();
        }

        public override void GoToIdle()
        {
            IdleRequests++;
            base.GoToIdle();
        }
    }
}
