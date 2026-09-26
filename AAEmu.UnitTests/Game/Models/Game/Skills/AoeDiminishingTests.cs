using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Plots.Type;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class AoeDiminishingTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly DamageProbe _damage = new();
    private readonly OtherProbe _other = new();
    private PlotState _state;

    [Before(Test)]
    public void SetUp()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE aoe_diminishings (id INTEGER, rate REAL);
            INSERT INTO aoe_diminishings VALUES (10,10),(1,100),(2,90),(3,80),(4,70),
                (5,55),(6,40),(7,25),(8,10),(9,10);
            """;
        command.ExecuteNonQuery();
        var data = new AoeDiminishingGameData();
        data.Load(connection);
        SetInstance(data);
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<string, Dictionary<uint, EffectTemplate>>
            {
                ["DamageEffect"] = new() { [1] = _damage },
                ["Probe"] = new() { [1] = _other },
                ["ResetAoeDiminishingEffect"] = new() { [2] = new ResetAoeDiminishingEffect() }
            });
        SetInstance(skills);
        _state = NewState();
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    public async Task AreaEvent_TwelveTargets_UsesAuthoredRatesAndFinalTail()
    {
        Apply(_state, Enumerable.Range(1, 12).Select(id => new Unit { ObjId = (uint)id }).ToArray());

        await Assert.That(_damage.Multipliers.SequenceEqual(
            [1f, .9f, .8f, .7f, .55f, .4f, .25f, .1f, .1f, .1f, .1f, .1f])).IsTrue();
    }

    [Test]
    public async Task PerTargetChildEvents_ShareCounterAndKeepRepeatedTargetRate()
    {
        var first = new Unit { ObjId = 10 };
        var second = new Unit { ObjId = 11 };
        Apply(_state, [first], eventId: 2723);
        Apply(_state, [second], eventId: 2723);
        Apply(_state, [first], eventId: 2723);
        Apply(_state, [second], eventId: 999);

        await Assert.That(_damage.Multipliers.SequenceEqual([1f, .9f, 1f, .9f])).IsTrue();
    }

    [Test]
    public async Task AuthoredReset_StartsFreshTargetOrderForOnlyItsCast()
    {
        var first = new Unit { ObjId = 10 };
        var second = new Unit { ObjId = 11 };
        var otherCast = NewState();
        Apply(_state, [first, second]);
        Apply(otherCast, [first, second]);
        Apply(_state, [first], marked: false, effectType: "ResetAoeDiminishingEffect", actualId: 2);
        Apply(_state, [second, first]);
        Apply(otherCast, [second]);

        await Assert.That(_damage.Multipliers.SequenceEqual([1f, .9f, 1f, .9f, 1f, .9f, .9f])).IsTrue();
    }

    [Test]
    public async Task UnmarkedDamageAndOtherEffects_DoNotConsumeCounterOrInheritReduction()
    {
        Apply(_state, [new Unit { ObjId = 10 }], marked: false);
        Apply(_state, [new Unit { ObjId = 11 }], effectType: "Probe");
        Apply(_state, [new Unit { ObjId = 12 }]);
        Apply(_state, [new Unit { ObjId = 13 }], marked: false);
        Apply(_state, [new Unit { ObjId = 14 }]);

        await Assert.That(_damage.Multipliers.SequenceEqual([1f, 1f, 1f, .9f])).IsTrue();
        await Assert.That(_other.Multipliers.SequenceEqual([1f])).IsTrue();
        await Assert.That(new EffectSource().AoeDamageMultiplier).IsEqualTo(1f);
    }

    [Test]
    public async Task CancelledPlot_DoesNotConsumeTargetRanks()
    {
        _state.RequestCancellation();
        Apply(_state, [new Unit { ObjId = 10 }]);
        await Assert.That(_damage.Multipliers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentCasts_KeepIndependentTargetCounters()
    {
        var casts = Enumerable.Range(0, 8).Select(_ => NewState()).ToArray();
        var rates = await Task.WhenAll(casts.Select(state => Task.Run(() =>
            Enumerable.Range(1, 3).Select(id => state.GetAoeDamageMultiplier(new Unit { ObjId = (uint)id })).ToArray())));

        await Assert.That(rates.All(values => values.SequenceEqual([1f, .9f, .8f]))).IsTrue();
    }

    [Test]
    public async Task DamageEffect_ScalesDamageAbsorptionAndLifeSteal_BeforeEvents()
    {
        var caster = new DamageUnit { ObjId = 1, Hp = 100, MaxHp = 1000 };
        var target = new DamageUnit { ObjId = 2, Hp = 1000, MaxHp = 1000, Armor = 5300 };
        var damage = new DamageEffect
        {
            DamageType = DamageType.Melee, UseFixedDamage = true, FixedMin = 200, FixedMax = 200,
            HealthStealRatio = 100, CheckCrime = false
        };
        var eventDamage = -1;
        target.Events.OnDamaged += (_, args) => eventDamage = args.Amount;

        damage.Apply(caster, new SkillCasterUnit(1), target, new SkillCastUnitTarget(2), new CastSkill(1, 1),
            new EffectSource { AoeDamageMultiplier = .4f }, null, DateTime.UtcNow);

        await Assert.That(target.DamageReceived).IsEqualTo(40);
        await Assert.That(caster.Hp).IsEqualTo(140);
        await Assert.That(caster.SummarizeDamage).IsEqualTo(40);
        await Assert.That(eventDamage).IsEqualTo(40);
        var packet = target.Packets.OfType<SCUnitDamagedPacket>().Single().Write(new PacketStream());
        packet.ReadBytes(18); // CastSkill, SkillCasterUnit, source/target IDs, and crime state.
        await Assert.That(packet.ReadPiscW(3).SequenceEqual([40L, 40L, 0L])).IsTrue();
    }

    private static PlotState NewState()
    {
        var caster = new Unit { ObjId = 100 };
        return new PlotState(caster, new SkillCasterUnit(100), caster, new SkillCastUnitTarget(100), null,
            new Skill(new SkillTemplate { Id = 1 }));
    }

    private static void Apply(PlotState state, Unit[] targets, bool marked = true, uint eventId = 189,
        string effectType = "DamageEffect", uint actualId = 1)
    {
        var effect = new PlotEventEffect
        {
            SourceId = PlotEffectSource.OriginalSource, TargetId = PlotEffectTarget.Target,
            ActualId = actualId, ActualType = effectType
        };
        var info = new PlotTargetInfo(state.Caster, state.Target) { EffectedTargets = [.. targets] };
        byte flag = 2;
        effect.ApplyEffect(state, info, new PlotEventTemplate { Id = eventId, AoeDiminishing = marked }, ref flag);
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private sealed class DamageProbe : DamageEffect
    {
        public List<float> Multipliers { get; } = [];
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
            CompressedGamePackets packetBuilder = null) => Multipliers.Add(source.AoeDamageMultiplier);
    }

    private sealed class OtherProbe : EffectTemplate
    {
        public List<float> Multipliers { get; } = [];
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
            CompressedGamePackets packetBuilder = null) => Multipliers.Add(source.AoeDamageMultiplier);
    }

    private sealed class DamageUnit : Unit
    {
        public List<GamePacket> Packets { get; } = [];
        public int DamageReceived { get; private set; }
        public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
        {
            DamageReceived += value;
            Hp -= value;
        }
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }
}
