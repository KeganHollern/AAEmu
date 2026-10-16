using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class BuffExpiryTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private SkillManager _skills;
    private Mock<ITaskManager> _tasks;

    [Before(Test)]
    public void SetUp()
    {
        _skills = new SkillManager(null, null);
        BuffBreakerTestData.Configure(_skills);
        Install(_skills);
        _tasks = Mock.Of<ITaskManager>();
        Install(new EffectTaskManager(_tasks.Object));
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments(30000, 25000d, 0, 25000d)]
    [Arguments(30000, 25000d, 25000, 5000d)]
    [Arguments(30000, 25000d, 29000, 1000d)]
    [Arguments(30000, 25000d, 30000, 0d)]
    [Arguments(30000, 25000d, 35000, 0d)]
    [Arguments(5000, 1000d, 4000, 1000d)]
    [Arguments(5000, 1000d, 5000, 0d)]
    [Arguments(2000, 0d, 0, 2000d)]
    [Arguments(2000, 0d, 2000, 0d)]
    [Arguments(0, 1000d, 50000, 1000d)]
    [Arguments(0, 0d, 0, -1d)]
    public async Task NextDelay_NeverPassesFiniteExpiry(int duration, double tick, int elapsed, double expected)
    {
        var now = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        var buff = Create(duration, tick, now.AddMilliseconds(-elapsed));
        await Assert.That(buff.GetNextEffectDelay(now)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(5000, 1000d)]
    [Arguments(30000, 25000d)]
    [Arguments(2000, 0d)]
    public async Task FinalCallback_RemovesBonusesAtExpiryWithoutAnotherFullTick(int duration, double tick)
    {
        var buff = Create(duration, tick, DateTime.UtcNow.AddMilliseconds(-duration - 100));
        var owner = (ProbeUnit)buff.Owner;
        var modifier = new DynamicBonusTemplate { Attribute = UnitAttribute.MoveSpeedMul, FuncType = "LinearFunc" };
        buff.Template.DynamicBonuses.Add(modifier);
        owner.AddDynamicBonus(buff.Index, new DynamicBonus
        {
            Template = modifier, SourceBuff = buff,
            LinearFunc = new LinearFuncTemplate { StartValue = 150, EndValue = 150 }
        });
        SetCount(buff, 10);
        var timeouts = 0;
        buff.Events.OnTimeout += (_, _) => timeouts++;

        new DispelTask(buff).Execute();
        new DispelTask(buff).Execute();

        await Assert.That(buff.State).IsEqualTo(EffectState.Finished);
        await Assert.That(buff.InUse).IsFalse();
        await Assert.That(owner.GetDynamicBonuses(UnitAttribute.MoveSpeedMul)).IsEmpty();
        await Assert.That(timeouts).IsEqualTo(1);
        _tasks.Schedule(Any<AAEmu.Game.Models.Tasks.Task>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>())
            .WasCalled(Times.Never);
    }

    [Test]
    [Arguments(5000, 1000d, 1)]
    [Arguments(4800, 1000d, 0)]
    [Arguments(9000, 2000d, 0)]
    [Arguments(2000, 3000d, 0)]
    public async Task Expiry_OnlyAppliesAPeriodicEffectAtAFullTickBoundary(int duration, double tick, int expected)
    {
        var ticks = 0;
        var effect = new ProbeEffect(() => ticks++);
        typeof(SkillManager).GetField("_types", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_skills, new Dictionary<uint, EffectType> { [1] = new() { Type = "Probe", ActualId = 1 } });
        typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_skills, new Dictionary<string, Dictionary<uint, EffectTemplate>> { ["Probe"] = new() { [1] = effect } });
        var buff = Create(duration, tick, DateTime.UtcNow.AddMilliseconds(-duration - 100));
        buff.Template.TickEffects.Add(new TickEffect { EffectId = 1 });
        SetCount(buff, 2);

        new DispelTask(buff).Execute();
        new DispelTask(buff).Execute();

        await Assert.That(ticks).IsEqualTo(expected);
        await Assert.That(buff.State).IsEqualTo(EffectState.Finished);
    }

    [Test]
    public async Task TickBeforeExpiry_KeepsBuffAndSchedulesFinalPartialInterval()
    {
        var buff = Create(30000, 25000, DateTime.UtcNow.AddSeconds(-25));
        SetCount(buff, 2);
        new DispelTask(buff).Execute();
        await Assert.That(buff.State).IsEqualTo(EffectState.Acting);
        _tasks.Schedule(Any<AAEmu.Game.Models.Tasks.Task>(),
                Is<TimeSpan?>(delay => delay.HasValue && delay.Value.TotalMilliseconds > 0 && delay.Value.TotalMilliseconds <= 5000),
                Any<TimeSpan?>(), Any<int>())
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task NonExpiringTick_DoesNotEndAtFirstCallback()
    {
        var buff = Create(0, 1000, DateTime.UtcNow.AddHours(-1));
        SetCount(buff, -1);
        new DispelTask(buff).Execute();
        await Assert.That(buff.State).IsEqualTo(EffectState.Acting);
        _tasks.Schedule(Any<AAEmu.Game.Models.Tasks.Task>(), TimeSpan.FromMilliseconds(1000), Any<TimeSpan?>(), Any<int>())
            .WasCalled(Times.Once);
    }

    private static Buff Create(int duration, double tick, DateTime start)
    {
        var owner = new ProbeUnit();
        return new Buff(owner, owner, new SkillCasterUnit(1), new BuffTemplate { Id = 930, Tick = tick }, null, start)
        { Duration = duration, Tick = tick, State = EffectState.Acting, InUse = true, Index = 1 };
    }

    private static void SetCount(Buff buff, int count) => typeof(Buff)
        .GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(buff, count);

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private sealed class ProbeUnit : Unit
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class ProbeEffect(Action tick) : EffectTemplate
    {
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null)
        {
            tick();
        }
    }
}
