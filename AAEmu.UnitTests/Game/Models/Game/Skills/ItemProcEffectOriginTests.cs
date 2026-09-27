using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Buffs.Triggers;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.UnitTests.Utils.Mocks;

using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class ItemProcEffectOriginTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private SkillManager _skills;
    private TaskManager _tasks;

    [Before(Test)]
    public void SetUp()
    {
        _skills = new SkillManager(null, null);
        BuffBreakerTestData.Configure(_skills);
        SetInstance(_skills);
        _tasks = new TaskManager(null);
        SetInstance(_tasks);
        SetInstance(new EffectTaskManager(Mock.Of<ITaskManager>().Object));
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task Apply_BuffPotencyUsesItemLevelOnlyForItemProcs(bool itemProc, bool effectWrapper)
    {
        var owner = new CharacterMock { ObjId = 1 };
        var buffs = Mock.Of<IBuffs>();
        owner.Buffs = buffs.Object;
        var template = new BuffTemplate { Id = 4257 };
        template.Bonuses.Add(new BonusTemplate { Attribute = UnitAttribute.Str, Value = 10, LinearLevelBonus = 100 });
        var skill = new Skill(new SkillTemplate { AbilityLevel = 1 }) { IsItemProc = itemProc, Level = 50 };
        var source = new EffectSource(skill);

        if (effectWrapper)
            new BuffEffect { Buff = template, Chance = 100 }
                .Apply(owner, new SkillCasterUnit(1), owner, null, null, source, null, DateTime.UtcNow);
        else
            template.Apply(owner, new SkillCasterUnit(1), owner, null, null, source, null, DateTime.UtcNow);

        Buff applied = null;
        buffs.AddBuff(Is<Buff>(Capture), 0, 0).WasCalled(Times.Once);
        await Assert.That(applied.AbLevel).IsEqualTo(itemProc ? 50u : 1u);
        applied.Passive = true;
        template.Start(owner, owner, applied);
        await Assert.That(owner.CalculateWithBonuses(0d, UnitAttribute.Str)).IsEqualTo(itemProc ? 60d : 11d);

        bool Capture(Buff value)
        {
            applied = value;
            return true;
        }
    }

    [Test]
    public async Task BuffTick_ProcHealKeepsOriginWithoutAddingParentSkillDamageContext()
    {
        var owner = new Unit { ObjId = 1 };
        var capture = new CaptureEffect();
        RegisterEffect(12605, capture);
        // Proc19 -> skill15245 -> buff2262 -> periodic HealEffect195.
        var template = new BuffTemplate { Id = 2262 };
        template.TickEffects.Add(new TickEffect { EffectId = 12605 });
        var skill = new Skill(new SkillTemplate { Id = 15245, CastingTime = 3000 }) { IsItemProc = true, Level = 50 };
        var buff = new Buff(owner, owner, new SkillCasterUnit(1), template, skill, DateTime.UtcNow);

        template.TimeToTimeApply(owner, owner, buff);

        await Assert.That(capture.Source.IsItemProc).IsTrue();
        await Assert.That(capture.Source.ItemProcLevel).IsEqualTo((byte)50);
        await Assert.That(capture.Source.Skill).IsNull();
        await Assert.That(capture.Source.Buff).IsSameReferenceAs(template);
    }

    [Test]
    [Arguments("damage")]
    [Arguments("damaged")]
    [Arguments("attack")]
    [Arguments("death")]
    [Arguments("dispelled")]
    [Arguments("started")]
    [Arguments("timeout")]
    [Arguments("generic")]
    public async Task BuffTrigger_ProcOriginSurvivesEveryTriggerKind(string kind)
    {
        var owner = new Unit { ObjId = 1 };
        var capture = new CaptureEffect();
        var template = new BuffTriggerTemplate { Effect = capture };
        // Proc54 -> skill19059 -> buff4257 -> damage trigger -> HealEffect337.
        var buff = new Buff(owner, owner, new SkillCasterUnit(1), new BuffTemplate { Id = 4257 },
            new Skill(new SkillTemplate { Id = 19059 }) { IsItemProc = true, Level = 45 }, DateTime.UtcNow);
        BuffTrigger trigger = kind switch
        {
            "damage" => new DamageBuffTrigger(buff, template),
            "damaged" => new DamagedBuffTrigger(buff, template),
            "attack" => new AttackBuffTrigger(buff, template),
            "death" => new DeathBuffTrigger(buff, template),
            "dispelled" => new DispelledBuffTrigger(buff, template),
            "started" => new StartedBuffTrigger(buff, template),
            "timeout" => new TimeoutBuffTrigger(buff, template),
            _ => new BuffTrigger(buff, template)
        };

        trigger.Execute(owner, EventArgs.Empty);

        await Assert.That(capture.Source.IsItemProc).IsTrue();
        await Assert.That(capture.Source.ItemProcLevel).IsEqualTo((byte)45);
    }

    [Test]
    public async Task SkillUseTick_SchedulesAChildWithTheProcMarkerAndItemLevel()
    {
        var owner = new Unit { ObjId = 1 };
        var source = new EffectSource(new BuffTemplate { Id = 4367 }) { IsItemProc = true, ItemProcLevel = 50 };
        SetField(_skills, "_skills", new Dictionary<uint, SkillTemplate> { [19228] = new() { Id = 19228 } });
        // Proc66 -> skill19152 -> buff4367 -> special8144 SkillUse -> skill19228.
        var effect = new SpecialEffect { SpecialEffectTypeId = SpecialType.SkillUse, Value1 = 19228, Value2 = 50 };

        effect.Apply(owner, new SkillCasterUnit(1), owner, new SkillCastUnitTarget(1), null, source, null, DateTime.UtcNow);

        var tasks = (ConcurrentDictionary<uint, GameTask>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_tasks);
        var pending = tasks.Values.OfType<UseSkillTask>().Single();
        var child = (Skill)typeof(UseSkillTask).GetField("_skill", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending);
        await Assert.That(child.Id).IsEqualTo(19228u);
        await Assert.That(child.IsItemProc).IsTrue();
        await Assert.That(child.Level).IsEqualTo((byte)50);
    }

    [Test]
    public async Task ChildBuffWithoutSkill_KeepsOriginForItsNextTick()
    {
        var owner = new Unit { ObjId = 1 };
        var buffs = Mock.Of<IBuffs>();
        owner.Buffs = buffs.Object;
        var capture = new CaptureEffect();
        RegisterEffect(1, capture);
        var template = new BuffTemplate { Id = 2070 };
        template.TickEffects.Add(new TickEffect { EffectId = 1 });
        var source = new EffectSource(new BuffTemplate()) { IsItemProc = true, ItemProcLevel = 50 };

        new BuffEffect { Buff = template, Chance = 100 }
            .Apply(owner, new SkillCasterUnit(1), owner, null, null, source, null, DateTime.UtcNow);
        Buff child = null;
        buffs.AddBuff(Is<Buff>(Capture), 0, 0).WasCalled(Times.Once);
        template.TimeToTimeApply(owner, owner, child);

        await Assert.That(child.Skill).IsNull();
        await Assert.That(child.AbLevel).IsEqualTo(50u);
        await Assert.That(capture.Source.IsItemProc).IsTrue();
        await Assert.That(capture.Source.ItemProcLevel).IsEqualTo((byte)50);

        bool Capture(Buff value)
        {
            child = value;
            return true;
        }
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task RefreshOrOverwrite_ReplacesProcOriginInBothDirections(bool nextIsProc, bool overwrite)
    {
        var owner = new Unit { ObjId = 1 };
        var template = new BuffTemplate { Id = 4257 };
        var previousSkill = new Skill(new SkillTemplate { Id = 19059 }) { IsItemProc = !nextIsProc, Level = 50 };
        var nextSkill = new Skill(new SkillTemplate { Id = 19059 }) { IsItemProc = nextIsProc, Level = 30 };
        var previous = new Buff(owner, owner, new SkillCasterUnit(1), template, previousSkill, DateTime.UtcNow)
            { Passive = true, IsItemProc = !nextIsProc, ItemProcLevel = !nextIsProc ? (byte)50 : (byte)0 };
        var next = new Buff(owner, owner, new SkillCasterUnit(1), template, nextSkill, DateTime.UtcNow)
            { Passive = true, AbLevel = nextIsProc ? 30u : 1u };
        var capture = new CaptureEffect();
        var trigger = new BuffTrigger(previous, new BuffTriggerTemplate { Effect = capture });

        if (overwrite)
            previous.OverwriteWith(next);
        else
            previous.ApplyRefreshState(next, DateTime.UtcNow);
        trigger.Execute(owner, EventArgs.Empty);

        await Assert.That(previous.Skill).IsSameReferenceAs(nextSkill);
        await Assert.That(previous.AbLevel).IsEqualTo(next.AbLevel);
        await Assert.That(previous.IsItemProc).IsEqualTo(nextIsProc);
        await Assert.That(previous.ItemProcLevel).IsEqualTo(nextIsProc ? (byte)30 : (byte)0);
        await Assert.That(capture.Source.IsItemProc).IsEqualTo(nextIsProc);
        await Assert.That(capture.Source.ItemProcLevel).IsEqualTo(nextIsProc ? (byte)30 : (byte)0);
    }

    private void RegisterEffect(uint id, EffectTemplate effect)
    {
        SetField(_skills, "_types", new Dictionary<uint, EffectType> { [id] = new() { ActualId = id, Type = "Capture" } });
        SetField(_skills, "_effects", new Dictionary<string, Dictionary<uint, EffectTemplate>> { ["Capture"] = new() { [id] = effect } });
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous[field] = field.GetValue(null);
        field.SetValue(null, instance);
    }

    private static void SetField<T>(T instance, string name, object value)
    {
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    }

    private sealed class CaptureEffect : EffectTemplate
    {
        public EffectSource Source { get; private set; }
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null)
        {
            Source = source;
        }
    }
}
