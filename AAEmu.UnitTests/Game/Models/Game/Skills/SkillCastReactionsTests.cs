using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class SkillCastReactionsTests
{
    private readonly Dictionary<FieldInfo, object> _instances = [];
    private static readonly DateTime Start = new(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);

    [Before(Test)]
    public void SetUp()
    {
        Install(new SkillManager(null, null));
        Install(new DuelManager());
        Install(new ZoneManager(null, null));
        Install(new WorldManager(null, null, null, null, null));
        Install(new UnitRequirementsGameData());
        Install(new SkillRequirementsGameData());
        Install(new UnitAttributeLimitsGameData());
        Install(new TaskManager(null));
        var formulas = new FormulaManager();
        Install(formulas);
        typeof(FormulaManager).GetProperty("CalculationEngine")!.SetValue(formulas, new Jace.CalculationEngine());
        typeof(FormulaManager).GetField("_formulas", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(formulas,
            new Dictionary<uint, Formula>
            {
                [3] = new Formula("300 * (100 / casting_tolerance) * (0.42 * floor(damage_percent / 5))")
            });
        typeof(FormulaManager).GetField("_unitFormulas", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(formulas,
            new Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>>());
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _instances)
            field.SetValue(null, value);
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _instances.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static (TestUnit unit, Skill skill, CastTask task) CreateCast(bool delayable = true)
    {
        var unit = new TestUnit { Hp = 1000, MaxHp = 1000 };
        var skill = new Skill(new SkillTemplate { Id = 10107, CastingTime = 2000, CastingDelayable = delayable }) { TlId = 42 };
        var task = new CastTask(skill, unit, new SkillCasterUnit(unit.ObjId), unit, new SkillCastUnitTarget(unit.ObjId), null)
        {
            CastWindow = new CastWindow(Start.AddSeconds(2), true, false, delayable)
        };
        unit.SkillTask = task;
        return (unit, skill, task);
    }

    [Test]
    public async Task Movement_CancelsAnActiveCastWithoutClientStop_AndOldCallbackCannotFire()
    {
        var (unit, skill, task) = CreateCast();
        var callbacks = 0;
        skill.Callback = () => callbacks++;
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        task.Execute();
        SkillCastReactions.OnMovement(unit, Vector3.UnitX, Vector3.UnitY, true, false);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(unit.SkillTask).IsNull();
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(unit.Packets.OfType<SCCastingStoppedPacket>().Count()).IsEqualTo(1);
        await Assert.That(unit.Packets.OfType<SCSkillEndedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, true)]
    public async Task Rotation_UsesTheAuthoredTurnFlag(bool turned, bool stopByTurn, bool cancelled)
    {
        var (unit, skill, _) = CreateCast();
        skill.Template.StopCastingByTurn = stopByTurn;
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.Zero, true, turned);
        await Assert.That(skill.Cancelled).IsEqualTo(cancelled);
    }

    [Test]
    public async Task ParentChange_CancelsTheCastWhenTheActorMountsOrDismounts()
    {
        var (unit, skill, _) = CreateCast();
        SkillCastReactions.OnMovement(unit, Vector3.Zero, new Vector3(100), false, false);
        await Assert.That(skill.Cancelled).IsTrue();
    }

    [Test]
    public async Task CancellationCallback_CannotLoseTheNextCast()
    {
        var (unit, skill, task) = CreateCast();
        var next = CreateCast().task;
        skill.Callback = () => unit.SkillTask = next;
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        task.Execute();
        await Assert.That(unit.SkillTask).IsSameReferenceAs(next);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(49, 0)]
    [Arguments(50, 126)]
    [Arguments(99, 126)]
    [Arguments(100, 252)]
    [Arguments(200, 504)]
    public async Task Damage_UsesAuthoredFivePercentSteps(int damage, int delay)
    {
        var (unit, _, task) = CreateCast();
        SkillCastReactions.OnDamage(unit, damage, Start.AddMilliseconds(200));
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(Start.AddMilliseconds(2000 + delay));
        await Assert.That(unit.Packets.OfType<SCCastingDelayedPacket>().Count()).IsEqualTo(delay == 0 ? 0 : 1);
    }

    [Test]
    public async Task Damage_ExtendsFromCurrentDeadline_AndCompletedCastRejectsAnotherHit()
    {
        var (unit, _, task) = CreateCast();
        SkillCastReactions.OnDamage(unit, 100, Start);
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(task.CastWindow.TryComplete(Start.AddSeconds(2), out var remaining)).IsFalse();
        await Assert.That(remaining).IsEqualTo(TimeSpan.FromMilliseconds(504));
        await Assert.That(task.CastWindow.TryComplete(Start.AddMilliseconds(2504), out _)).IsTrue();
        SkillCastReactions.OnDamage(unit, 100, Start.AddMilliseconds(2504));
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(Start.AddMilliseconds(2504));
        await Assert.That(task.CastWindow.TryComplete(Start.AddMilliseconds(2504), out _)).IsFalse();
    }

    [Test]
    public async Task DueTask_QueuesAFreshWakeupIfDamageExtendedTheDeadline()
    {
        var (unit, skill, task) = CreateCast();
        task.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(5), true, false, true);
        task.Execute();
        await Assert.That(unit.SkillTask).IsSameReferenceAs(task);
        await Assert.That(skill.Cancelled).IsFalse();
        var queue = typeof(TaskManager).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(TaskManager.Instance);
        var count = (int)queue!.GetType().GetProperty("Count")!.GetValue(queue)!;
        await Assert.That(count).IsEqualTo(1);
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        task.Execute();
        await Assert.That(unit.Packets.OfType<SCSkillEndedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Damage_DoesNotDelayAnUnmarkedCast_OrAChannel()
    {
        var (unit, _, task) = CreateCast(false);
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(Start.AddSeconds(2));
        task.CastWindow = new CastWindow(Start.AddSeconds(2), false, true, true);
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(Start.AddSeconds(2));
        await Assert.That(unit.Packets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Damage_UsesHealthLostAfterAbsorption_AndDoesNotDelayLethalDamage()
    {
        var (unit, _, task) = CreateCast();
        task.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(5), true, false, true);
        var deadline = task.CastWindow.Deadline;
        unit.ReduceCurrentHp(null, 50);
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(deadline.AddMilliseconds(126));
        unit.ReduceCurrentHp(null, 0);
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(deadline.AddMilliseconds(126));
        unit.ReduceCurrentHp(null, 950);
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(deadline.AddMilliseconds(126));
    }

    [Test]
    public async Task PlotFanOut_DelaysEveryMarkedWaitOnce_AndPreservesOrdinaryDelays()
    {
        var (unit, skill, _) = CreateCast();
        unit.SkillTask = null;
        var state = new PlotState(unit, null, unit, null, null, skill);
        unit.ActivePlotState = state;
        var first = state.RegisterCastWait(new PlotNextEvent { Casting = true, CastingDelayable = true }, Start.AddSeconds(2));
        var second = state.RegisterCastWait(new PlotNextEvent { Casting = true, CastingDelayable = true }, Start.AddSeconds(3));
        var fixedWait = state.RegisterCastWait(new PlotNextEvent { Casting = true }, Start.AddSeconds(4));
        var ordinary = state.RegisterCastWait(new PlotNextEvent { Delay = 5000 }, Start.AddSeconds(5));
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(first.Deadline).IsEqualTo(Start.AddMilliseconds(2252));
        await Assert.That(second.Deadline).IsEqualTo(Start.AddMilliseconds(3252));
        await Assert.That(fixedWait.Deadline).IsEqualTo(Start.AddSeconds(4));
        await Assert.That(ordinary).IsNull();
        await Assert.That(unit.Packets.OfType<SCCastingDelayedPacket>().Count()).IsEqualTo(1);
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        await Assert.That(state.CancellationRequested()).IsTrue();
    }

    [Test]
    public async Task Movement_AfterTheCastWindow_PreservesThePlotProjectileAndInstantSkill()
    {
        var (unit, skill, _) = CreateCast();
        unit.SkillTask = null;
        var state = new PlotState(unit, null, unit, null, null, skill);
        unit.ActivePlotState = state;
        var wait = state.RegisterCastWait(new PlotNextEvent { Casting = true }, Start);
        wait.TryComplete(Start, out _);
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        await Assert.That(state.CancellationRequested()).IsFalse();
        await Assert.That(skill.Cancelled).IsFalse();
    }

    [Test]
    public async Task StationaryPosition_RoundTripDoesNotCancelTheCast()
    {
        var (unit, skill, _) = CreateCast();
        var position = new Vector3(12.3456f, 23.4567f, 34.5678f);
        var (x, y, z) = Helpers.ConvertPosition(Helpers.ConvertPosition(position.X, position.Y, position.Z));
        SkillCastReactions.OnMovement(unit, position, new Vector3(x, y, z), true, false);
        await Assert.That(skill.Cancelled).IsFalse();
    }

    [Test]
    public async Task CastingTolerance_ModifiersChangeTheAuthoredDelay_AndRemovalRestoresIt()
    {
        var (unit, _, _) = CreateCast();
        unit.AddBonus(2, new Bonus
        {
            Template = new BonusTemplate { Attribute = UnitAttribute.CastingTolerance, ModifierType = UnitModifierType.Value },
            Value = 100
        });
        await Assert.That(SkillCastReactions.CalculateDamageDelay(unit, 100)).IsEqualTo(126);
        unit.RemoveBonus(2, UnitAttribute.CastingTolerance);
        await Assert.That(SkillCastReactions.CalculateDamageDelay(unit, 100)).IsEqualTo(252);
        unit.AddBonus(2, new Bonus
        {
            Template = new BonusTemplate { Attribute = UnitAttribute.CastingTolerance, ModifierType = UnitModifierType.Value },
            Value = -100
        });
        await Assert.That(SkillCastReactions.CalculateDamageDelay(unit, 100)).IsEqualTo(0);
    }

    [Test]
    public async Task FullyAbsorbedDamage_DoesNotPushBackTheCast()
    {
        var (unit, skill, task) = CreateCast();
        task.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(5), true, false, true);
        var deadline = task.CastWindow.Deadline;
        var shield = new Buff(unit, unit, new SkillCasterUnit(1), new BuffTemplate { DamageAbsorptionTypeId = 1 }, skill, Start)
        {
            Charge = 1000
        };
        var buffs = Mock.Of<IBuffs>();
        buffs.GetAbsorptionEffects().Returns([shield]);
        unit.Buffs = buffs.Object;
        unit.ReduceCurrentHp(null, 100);
        await Assert.That(unit.Hp).IsEqualTo(1000);
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(deadline);
        await Assert.That(unit.Packets.OfType<SCCastingDelayedPacket>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task ChannelMovement_EndsOnce_AndStaleEndTaskCannotEndTheNextCast()
    {
        var (unit, skill, _) = CreateCast();
        skill.Template.ChannelingTime = 1000;
        var task = new EndChannelingTask(skill, unit, null, unit, null, null, null)
        {
            CastWindow = new CastWindow(Start.AddSeconds(1), false, true, false)
        };
        unit.SkillTask = task;
        var callbacks = 0;
        var next = CreateCast().task;
        skill.Callback = () => { callbacks++; unit.SkillTask = next; };
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        task.Execute();
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(unit.SkillTask).IsSameReferenceAs(next);
        await Assert.That(unit.Packets.OfType<SCSkillEndedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task PlotTree_UsesTheExtendedWait_AndCancelsBeforeTheChildEvent()
    {
        var (unit, skill, _) = CreateCast();
        unit.SkillTask = null;
        var state = new PlotState(unit, new SkillCasterUnit(1), unit, new SkillCastUnitTarget(1), null, skill);
        unit.ActivePlotState = state;
        var first = new PlotEventTemplate { Id = 1, SourceUpdateMethodId = 1, TargetUpdateMethodId = 1 };
        var second = new PlotEventTemplate { Id = 2, SourceUpdateMethodId = 1, TargetUpdateMethodId = 1 };
        var next = new PlotNextEvent { Casting = true, CastingDelayable = true, Delay = 5000, Event = second };
        first.NextEvents.AddLast(next);
        var root = new PlotNode { Event = first };
        root.Children.Add(new PlotNode { Event = second, Parent = root, ParentNextEvent = next });
        var tree = new PlotTree(280) { RootNode = root };
        var running = tree.ExecuteAsync(state);
        await Assert.That(state.IsCasting).IsTrue();
        var waits = (List<CastWindow>)typeof(PlotState).GetField("_castWaits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        var deadline = waits.Single().Deadline;
        SkillCastReactions.OnDamage(unit, 100, DateTime.UtcNow);
        await Assert.That(waits.Single().Deadline).IsEqualTo(deadline.AddMilliseconds(252));
        await Assert.That(running.IsCompleted).IsFalse();
        SkillCastReactions.OnMovement(unit, Vector3.Zero, Vector3.UnitX, true, false);
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(state.Tickets.ContainsKey(2)).IsFalse();
        await Assert.That(unit.ActivePlotState).IsNull();
        await Assert.That(unit.Packets.OfType<SCPlotCastingStoppedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ReplacedOrEndedCast_DoesNotDelayOrBroadcastForItsOldTimeline()
    {
        var (unit, skill, task) = CreateCast();
        var next = CreateCast(false).task;
        unit.SkillTask = next;
        SkillCastReactions.OnDamage(unit, 100, Start);
        task.Execute();
        await Assert.That(task.CastWindow.Active).IsFalse();
        await Assert.That(task.CastWindow.Deadline).IsEqualTo(Start.AddSeconds(2));
        await Assert.That(unit.Packets.OfType<SCCastingDelayedPacket>().Count()).IsEqualTo(0);
        unit.SkillTask = null;
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(unit.Packets.OfType<SCCastingDelayedPacket>().Count()).IsEqualTo(0);
        await Assert.That(skill.Cancelled).IsFalse();
    }

    [Test]
    public async Task CompletedOrExpiredPlotWait_DoesNotDelayOrBroadcast()
    {
        var (unit, skill, _) = CreateCast();
        unit.SkillTask = null;
        var state = new PlotState(unit, null, unit, null, null, skill);
        unit.ActivePlotState = state;
        var wait = state.RegisterCastWait(new PlotNextEvent { Casting = true, CastingDelayable = true }, Start);
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(wait.Deadline).IsEqualTo(Start);
        wait.TryComplete(Start, out _);
        SkillCastReactions.OnDamage(unit, 100, Start);
        await Assert.That(unit.Packets.OfType<SCCastingDelayedPacket>().Count()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewCastAdmission_StopsTheOldTimelineOnlyAfterSuccess(bool reject)
    {
        var (unit, oldSkill, oldTask) = CreateCast();
        unit.ObjId = 70;
        unit.Mp = 100;
        oldSkill.TlId = SkillTlIdManager.GetNextId(unit);
        var oldTl = oldSkill.TlId;
        var callbacks = 0;
        oldSkill.Callback = () => callbacks++;
        var world = new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        }, 0, true, 0);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unit, world);
        world.AddObject(unit);
        var incoming = new Skill(new SkillTemplate
        {
            Id = 50, TargetType = SkillTargetType.Self, DefaultGcd = true, CastingTime = 5000
        });
        if (reject)
            unit.GlobalCooldown = DateTime.UtcNow.AddSeconds(1);
        var result = incoming.Use(unit, new SkillCasterUnit(70), new SkillCastUnitTarget(70), null, false, out _);
        await Assert.That(result).IsEqualTo(reject ? SkillResult.CooldownTime : SkillResult.Success);
        await Assert.That(oldSkill.Cancelled).IsEqualTo(!reject);
        await Assert.That(callbacks).IsEqualTo(reject ? 0 : 1);
        if (reject)
        {
            await Assert.That(unit.SkillTask).IsSameReferenceAs(oldTask);
            await Assert.That(oldSkill.TlId).IsEqualTo(oldTl);
        }
        else
        {
            var allocated = (bool[])typeof(SkillTlIdManager).GetProperty("AssignedTl", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            await Assert.That(allocated[oldTl]).IsFalse();
            await Assert.That(unit.SkillTask.Skill).IsSameReferenceAs(incoming);
            oldTask.Execute();
            await Assert.That(unit.SkillTask.Skill).IsSameReferenceAs(incoming);
            await Assert.That(unit.Packets[0] is SCCastingStoppedPacket).IsTrue();
            await Assert.That(unit.Packets[1] is SCSkillEndedPacket).IsTrue();
            await Assert.That(unit.Packets[2] is SCSkillStartedPacket).IsTrue();
        }
        unit.SkillTask.Skill.Stop(unit);
    }

    [Test]
    public async Task CompletionAndCancellation_OnlyOneCanClaimTheWindow()
    {
        for (var index = 0; index < 100; index++)
        {
            var wait = new CastWindow(Start, true, false, true);
            var results = await Task.WhenAll(Task.Run(() => wait.TryCancel()), Task.Run(() => wait.TryComplete(Start, out _)));
            await Assert.That(results.Count(value => value)).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments((ushort)42, (ushort)0, 126u)]
    [Arguments((ushort)0, (ushort)65535, 252u)]
    [Arguments((ushort)42, (ushort)43, uint.MaxValue)]
    public async Task DelayedPacket_WritesBothTimelinesAndMilliseconds(ushort skill, ushort plot, uint delay)
    {
        var stream = new PacketStream();
        new SCCastingDelayedPacket(skill, plot, delay).Write(stream);
        await Assert.That(stream.ReadUInt16()).IsEqualTo(skill);
        await Assert.That(stream.ReadUInt16()).IsEqualTo(plot);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(delay);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    private sealed class TestUnit : Unit
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
        public override void PostUpdateCurrentHp(BaseUnit attackerBase, int oldHpValue, int newHpValue,
            KillReason killReason = KillReason.Damage) { }
    }
}
