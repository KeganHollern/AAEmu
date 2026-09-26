using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Animation;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;
using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Skills;

[NotInParallel]
public sealed class AttackTimingTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private TaskManager _tasks;

    [Before(Test)]
    public void SetUp()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unit_attribute_limits(unit_attribute_id INTEGER, minimum INTEGER, maximum INTEGER); " +
                              "INSERT INTO unit_attribute_limits VALUES (54,-800,4000),(55,-800,4000),(119,-800,4000)";
        command.ExecuteNonQuery();
        var limits = new UnitAttributeLimitsGameData();
        limits.Load(connection);
        Replace(limits);

        var tick = Mock.Of<ITickManager>();
        tick.OnTick.Returns(new TickManager.TickEventHandler());
        _tasks = new TaskManager(tick.Object);
        Replace(_tasks);
    }

    [After(Test)]
    public void TearDown()
    {
        _tasks.Stop();
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, 500, 1000d)]
    [Arguments(4u, UnitAttribute.RangedSpeedMul, 500, 1200d)]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, -600, 3750d)]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, -900, 7500d)]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, 9000, 300d)]
    [Arguments(4u, UnitAttribute.RangedSpeedMul, 9000, 360d)]
    public async Task AutoAttack_UsesBoundedRawSpeedWithoutTheOldIntervalClamp(uint skillId, UnitAttribute attribute,
        int raw, double expected)
    {
        var character = new CharacterMock();
        character.AddBonus(1, Modifier(attribute, raw));
        character.AddBonus(2, Modifier(UnitAttribute.GlobalCooldownMul, 9000));
        character.AddBonus(3, Modifier(UnitAttribute.AttackAnimSpeedMul, -800));
        var skill = new SkillTemplate { Id = skillId };
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(expected);
        character.RemoveBonus(1, attribute);
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(skillId == 4 ? 1800d : 1500d);
    }

    [Test]
    public async Task AutoAttack_SelectsTheEquippedWeaponAndOnlyItsSpeedAttribute()
    {
        var character = new CharacterMock();
        Equip(character, EquipmentItemSlot.Mainhand, 2000, EquipmentItemSlotType.OneHanded);
        Equip(character, EquipmentItemSlot.Ranged, 1000, EquipmentItemSlotType.Ranged);
        character.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, 1000));
        character.AddBonus(2, Modifier(UnitAttribute.RangedSpeedMul, -500));
        await Assert.That(SkillManager.GetAttackDelay(new SkillTemplate { Id = 2 }, character)).IsEqualTo(1000d);
        await Assert.That(SkillManager.GetAttackDelay(new SkillTemplate { Id = 4 }, character)).IsEqualTo(2000d);
        character.Equipment.Items.Clear();
        Equip(character, EquipmentItemSlot.Mainhand, 1400, EquipmentItemSlotType.OneHanded);
        await Assert.That(SkillManager.GetAttackDelay(new SkillTemplate { Id = 2 }, character)).IsEqualTo(700d);
    }

    [Test]
    public async Task TwoHandedSpeed_JoinsTheBoundedMeleeBonusAndKeepsTheNativeDenominatorFloor()
    {
        var character = new CharacterMock();
        Equip(character, EquipmentItemSlot.Mainhand, 2000, EquipmentItemSlotType.TwoHanded);
        character.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, 500));
        character.AddBonus(2, Modifier(UnitAttribute.TwohandSpeedMul, 500));
        var skill = new SkillTemplate { Id = 2 };
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(1000d);
        character.RemoveBonus(2, UnitAttribute.TwohandSpeedMul);
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(1333d);
        character.RemoveBonus(1, UnitAttribute.MeleeSpeedMul);
        character.AddBonus(3, Modifier(UnitAttribute.TwohandSpeedMul, -2000));
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(2000000d);
        character.Equipment.Items.Clear();
        Equip(character, EquipmentItemSlot.Mainhand, 2000, EquipmentItemSlotType.OneHanded);
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(2000d);
    }

    [Test]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, 937d, 600d)]
    [Arguments(4u, UnitAttribute.RangedSpeedMul, 1125d, 720d)]
    public async Task Speed_DynamicAndPercentBonusesKeepTheirRawUnitsAndReadTime(uint skillId, UnitAttribute attribute,
        double first, double last)
    {
        var character = new CharacterMock();
        character.AddBonus(1, Modifier(attribute, 400));
        character.AddBonus(2, Modifier(attribute, 50, UnitModifierType.Percent));
        var source = new Buff(character, character, new SkillCasterUnit(1), new BuffTemplate { Id = 1 }, null, DateTime.UtcNow)
            { Duration = 1000, StartTime = DateTime.UtcNow.AddSeconds(10) };
        character.AddDynamicBonus(3, new DynamicBonus
        {
            Template = new DynamicBonusTemplate { Attribute = attribute, ModifierType = UnitModifierType.Value, FuncType = "LinearFunc" },
            SourceBuff = source, LinearFunc = new LinearFuncTemplate { StartValue = 0, EndValue = 600 }
        });
        var skill = new SkillTemplate { Id = skillId };
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(first);
        source.StartTime = DateTime.UtcNow.AddSeconds(-10);
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(last);
        character.RemoveDynamicBonus(3, attribute);
        await Assert.That(SkillManager.GetAttackDelay(skill, character)).IsEqualTo(first);
    }

    [Test]
    public async Task Speed_TruncatesRawAttributeBeforeTheIntervalAndKeepsAnIntervalPositive()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, 1));
        unit.AddBonus(2, Modifier(UnitAttribute.MeleeSpeedMul, 90, UnitModifierType.Percent));
        await Assert.That(AttackTiming.GetWeaponInterval(unit, 1000, false, false)).IsEqualTo(999d);
        await Assert.That(AttackTiming.GetWeaponInterval(unit, 0.1, false, false)).IsEqualTo(1d);
    }

    [Test]
    [Arguments(0, 1000)]
    [Arguments(1000, 500)]
    [Arguments(-700, 3333)]
    [Arguments(-900, 5000)]
    [Arguments(9000, 200)]
    public async Task AnimationTime_UsesItsOwnBoundedRawRate(int raw, int expected)
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.AttackAnimSpeedMul, raw));
        unit.AddBonus(2, Modifier(UnitAttribute.MeleeSpeedMul, 4000));
        unit.AddBonus(3, Modifier(UnitAttribute.GlobalCooldownMul, -800));
        await Assert.That(AttackTiming.ScaleAnimationTime(unit, 1000)).IsEqualTo(expected);
        unit.RemoveBonus(1, UnitAttribute.AttackAnimSpeedMul);
        await Assert.That(AttackTiming.ScaleAnimationTime(unit, 1000)).IsEqualTo(1000);
    }

    [Test]
    public async Task AnimationTime_ReadsDynamicContributionsAndStopsAfterRemoval()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.AttackAnimSpeedMul, 1000));
        var source = new Buff(unit, unit, new SkillCasterUnit(1), new BuffTemplate { Id = 1 }, null, DateTime.UtcNow)
            { Duration = 1000, StartTime = DateTime.UtcNow.AddSeconds(10) };
        unit.AddDynamicBonus(2, new DynamicBonus
        {
            Template = new DynamicBonusTemplate { Attribute = UnitAttribute.AttackAnimSpeedMul, ModifierType = UnitModifierType.Value, FuncType = "LinearFunc" },
            SourceBuff = source, LinearFunc = new LinearFuncTemplate { StartValue = 0, EndValue = 1000 }
        });
        await Assert.That(AttackTiming.ScaleAnimationTime(unit, 1000)).IsEqualTo(500);
        source.StartTime = DateTime.UtcNow.AddSeconds(-10);
        await Assert.That(AttackTiming.ScaleAnimationTime(unit, 1000)).IsEqualTo(333);
        unit.RemoveDynamicBonus(2, UnitAttribute.AttackAnimSpeedMul);
        await Assert.That(AttackTiming.ScaleAnimationTime(unit, 1000)).IsEqualTo(500);
    }

    [Test]
    [Arguments(true, 700)]
    [Arguments(false, 100)]
    public async Task SkillEffects_OnlyScaleTheEnabledAnimationTime(bool useAnimTime, int expected)
    {
        var caster = new RecordingCharacter();
        caster.AddBonus(1, Modifier(UnitAttribute.AttackAnimSpeedMul, 1000));
        caster.AddBonus(2, Modifier(UnitAttribute.GlobalCooldownMul, 4000));
        var skill = new Skill(new SkillTemplate
        {
            Id = 50000, EffectDelay = 100, UseAnimTime = useAnimTime,
            FireAnim = new Anim { Id = 12, CombatSyncTime = 1200 }
        });
        var before = DateTime.UtcNow;
        skill.ScheduleEffects(caster, new SkillCasterUnit(1), caster, new SkillCastUnitTarget(1), null);
        var after = DateTime.UtcNow;
        var packet = caster.Packets.OfType<SCSkillFiredPacket>().Single();
        await Assert.That(packet.ComputedDelay).IsEqualTo((short)expected);
        var task = QueuedTasks().Single();
        await Assert.That(task.TriggerTime >= before.AddMilliseconds(expected)).IsTrue();
        await Assert.That(task.TriggerTime <= after.AddMilliseconds(expected)).IsTrue();
    }

    [Test]
    public async Task PlotDelay_ScalesCombatSyncTimeWithoutScalingTheSeparateDelay()
    {
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(skills,
            new Dictionary<string, Dictionary<uint, EffectTemplate>>
            {
                ["SpecialEffect"] = new() { [1] = new SpecialEffect { SpecialEffectTypeId = SpecialType.Anim, Value1 = 12 } }
            });
        Replace(skills);
        var animations = new AnimationManager();
        typeof(AnimationManager).GetField("_animations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(animations,
            new Dictionary<uint, Anim> { [12] = new Anim { Id = 12, CombatSyncTime = 1200 } });
        Replace(animations);
        var caster = new Unit();
        caster.AddBonus(1, Modifier(UnitAttribute.AttackAnimSpeedMul, 1000));
        var state = new PlotState(caster, null, caster, null, null, new Skill(new SkillTemplate()));
        var node = new PlotNode
        {
            Event = new PlotEventTemplate { Effects = new LinkedList<PlotEventEffect>([new PlotEventEffect { ActualType = "SpecialEffect", ActualId = 1 }]) }
        };
        var next = new PlotNextEvent { AddAnimCsTime = true, Delay = 100 };
        await Assert.That(next.GetDelay(state, new PlotTargetInfo(caster, caster), node)).IsEqualTo(700);
        caster.RemoveBonus(1, UnitAttribute.AttackAnimSpeedMul);
        await Assert.That(next.GetDelay(state, new PlotTargetInfo(caster, caster), node)).IsEqualTo(1300);
        next.AddAnimCsTime = false;
        await Assert.That(next.GetDelay(state, new PlotTargetInfo(caster, caster), node)).IsEqualTo(100);
    }

    [Test]
    public async Task PausedAutoAttack_RefreshesThePendingIntervalAndRestoresItAfterBuffRemoval()
    {
        var caster = new CharacterMock { ObjId = 1, Hp = 100, CurrentTarget = new Unit { ObjId = 2, Hp = 100 },
            IsAutoAttack = true, GlobalCooldown = DateTime.UtcNow.AddHours(1) };
        var task = new UseAutoAttackSkillTask(new Skill(new SkillTemplate { Id = 2 }), caster);
        caster.AutoAttackTask = task;
        _tasks.Schedule(task, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));
        var originalTrigger = task.TriggerTime;
        caster.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, 500));
        task.Execute();
        await Assert.That(task.RepeatInterval).IsEqualTo(TimeSpan.FromMilliseconds(1000));
        await Assert.That(task.TriggerTime).IsEqualTo(originalTrigger.AddMilliseconds(-500));
        caster.RemoveBonus(1, UnitAttribute.MeleeSpeedMul);
        task.Execute();
        await Assert.That(task.RepeatInterval).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(task.TriggerTime).IsEqualTo(originalTrigger);
        caster.Hp = 0;
        task.Execute();
        await Assert.That(caster.AutoAttackTask).IsNull();
        await Assert.That(caster.IsAutoAttack).IsFalse();
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(0);
    }

    [Test]
    public async Task StaleAutoAttack_DoesNotClearOrCancelTheReplacementTask()
    {
        var caster = new CharacterMock { IsAutoAttack = true };
        var skill = new Skill(new SkillTemplate { Id = 2 });
        var oldTask = new UseAutoAttackSkillTask(skill, caster);
        _tasks.Schedule(oldTask, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));
        _tasks.Cancel(oldTask);
        var newTask = new UseAutoAttackSkillTask(skill, caster);
        _tasks.Schedule(newTask, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));
        oldTask.Id = newTask.Id; // Simulate an ID reused after the old callback left the queue.
        caster.AutoAttackTask = newTask;
        oldTask.Execute();
        await Assert.That(ReferenceEquals(caster.AutoAttackTask, newTask)).IsTrue();
        await Assert.That(caster.IsAutoAttack).IsTrue();
        await Assert.That(newTask.Cancelled).IsFalse();
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(1);
    }

    [Test]
    public async Task ExactClient_AuthoredSpeedRowsUseTheConfirmedTimingPaths()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        UnitAttributeLimitsGameData.Instance.Load(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,unit_attribute_id,value FROM unit_modifiers " +
                              "WHERE id IN (12099,12101,24601,24622) AND owner_type='Buff' AND unit_modifier_type_id=0";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var caster = new CharacterMock();
            var attribute = (UnitAttribute)reader.GetInt32(1);
            caster.AddBonus(1, Modifier(attribute, reader.GetInt32(2)));
            switch (reader.GetInt32(0))
            {
                case 12099:
                    await Assert.That(SkillManager.GetAttackDelay(new SkillTemplate { Id = 2 }, caster)).IsEqualTo(1000d);
                    break;
                case 12101:
                    await Assert.That(SkillManager.GetAttackDelay(new SkillTemplate { Id = 4 }, caster)).IsEqualTo(1200d);
                    break;
                case 24601:
                    await Assert.That(AttackTiming.ScaleAnimationTime(caster, 1000)).IsEqualTo(588);
                    break;
                case 24622:
                    await Assert.That(AttackTiming.ScaleAnimationTime(caster, 1000)).IsEqualTo(3333);
                    break;
            }
            count++;
        }
        await Assert.That(count).IsEqualTo(4);
    }

    [Test]
    public async Task OverlappingRangedImpacts_KeepSeparateSkillsTargetsAndTimelines()
    {
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_skillReagents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillReagent>());
        typeof(SkillManager).GetField("_skillProducts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillProduct>());
        Replace(skills);
        Replace(new DuelManager());
        Replace(new ZoneManager(null, null));
        Replace(new WorldManager(null, null, null, null, null));
        Replace(new UnitRequirementsGameData());
        Replace(new SkillRequirementsGameData());
        Replace(new AchievementGameData());
        Replace(new PermissionManager(null));
        var models = new ModelManager();
        var modelTypes = typeof(ModelManager).GetField("_modelTypes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        modelTypes.SetValue(models, Activator.CreateInstance(modelTypes.FieldType));
        Replace(models);
        var caster = new RecordingCharacter { Id = 1, ObjId = 1, Hp = 100, Mp = 100, Level = 1, IsAutoAttack = true };
        var firstTarget = new Unit { ObjId = 2, Hp = 100 };
        var secondTarget = new Unit { ObjId = 3, Hp = 100 };
        firstTarget.Transform.Local.SetPosition(20, 0, 0);
        secondTarget.Transform.Local.SetPosition(28, 0, 0);
        caster.AddBonus(1, Modifier(UnitAttribute.RangedSpeedMul, 4000));
        var world = new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        }, 0, true, 0);
        foreach (var unit in new Unit[] { caster, firstTarget, secondTarget })
        {
            typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unit, world);
            world.AddObject(unit);
        }
        var effect = new RecordingImpact();
        var template = new SkillTemplate
        {
            Id = 4, SourceAlive = true, TargetAlive = true, TargetType = SkillTargetType.AnyUnit,
            EffectSpeed = 40, MaxRange = 30, AbilityId = AbilityType.General
        };
        template.Effects.Add(new SkillEffect
        {
            Template = effect, ApplicationMethod = SkillEffectApplicationMethod.Target, StartLevel = 0, EndLevel = 255,
            Chance = 100, Friendly = true, NonFriendly = true
        });
        var callbacks = 0;
        var options = new Skill(template) { Level = 7, CastTimeMultiplier = 0.5f, BaseCastingTime = 0,
            AutoAttackIndex = 3, Callback = () => callbacks++ };
        var task = new UseAutoAttackSkillTask(options, caster);
        caster.AutoAttackTask = task;
        _tasks.Schedule(task, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(200));
        caster.CurrentTarget = firstTarget;
        task.Execute();
        var firstPacket = caster.Packets.OfType<SCSkillFiredPacket>().Single();
        var field = typeof(SCSkillFiredPacket).GetField("_skill", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (Skill)field.GetValue(firstPacket)!;
        var firstTimeline = first.TlId;
        caster.CurrentTarget = secondTarget;
        task.Execute();
        var secondPacket = caster.Packets.OfType<SCSkillFiredPacket>().Last();
        var second = (Skill)field.GetValue(secondPacket)!;
        var secondTimeline = second.TlId;
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(ReferenceEquals(first, options)).IsFalse();
        await Assert.That(firstTimeline).IsNotEqualTo((ushort)0);
        await Assert.That(secondTimeline).IsNotEqualTo(firstTimeline);
        await Assert.That(first.InitialTarget).IsSameReferenceAs(firstTarget);
        await Assert.That(second.InitialTarget).IsSameReferenceAs(secondTarget);
        await Assert.That(first.Level).IsEqualTo((byte)7);
        await Assert.That(second.CastTimeMultiplier).IsEqualTo(0.5f);
        await Assert.That(second.BaseCastingTime).IsEqualTo(0);
        await Assert.That(task.RepeatInterval).IsEqualTo(TimeSpan.FromMilliseconds(360));
        await Assert.That(firstPacket.ComputedDelay).IsEqualTo((short)500);
        await Assert.That(secondPacket.ComputedDelay).IsEqualTo((short)700);
        await Assert.That(firstPacket.FireAnimId).IsEqualTo(2u);
        await Assert.That(secondPacket.FireAnimId).IsEqualTo(1u);
        var impacts = QueuedTasks().OfType<ApplySkillTask>().OrderBy(impact => impact.Id).ToArray();
        await Assert.That(impacts.Length).IsEqualTo(2);
        impacts[0].Execute();
        await Assert.That(first.TlId).IsEqualTo((ushort)0);
        await Assert.That(second.TlId).IsEqualTo(secondTimeline);
        impacts[1].Execute();
        await Assert.That(second.TlId).IsEqualTo((ushort)0);
        await Assert.That(effect.Hits.Count).IsEqualTo(2);
        await Assert.That(effect.Hits[0]).IsEqualTo((2u, firstTimeline));
        await Assert.That(effect.Hits[1]).IsEqualTo((3u, secondTimeline));
        await Assert.That(callbacks).IsEqualTo(2);
        await Assert.That(caster.AutoAttackTask).IsSameReferenceAs(task);
    }

    private sealed class RecordingImpact : EffectTemplate
    {
        public List<(uint Target, ushort Timeline)> Hits { get; } = [];
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null)
        {
            Hits.Add((target.ObjId, source.Skill.TlId));
        }
    }

    private IEnumerable<GameTask> QueuedTasks() =>
        ((ConcurrentDictionary<uint, GameTask>)typeof(TaskManager).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_tasks)!).Values;

    private void Replace<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous[field] = field.GetValue(null);
        field.SetValue(null, manager);
    }

    private static void Equip(CharacterMock owner, EquipmentItemSlot slot, int speed, EquipmentItemSlotType type) =>
        owner.Equipment.Items.Add(new Weapon
        {
            Slot = (int)slot,
            Template = new WeaponTemplate { HoldableTemplate = new Holdable { Speed = speed, SlotTypeId = (uint)type } }
        });

    private static Bonus Modifier(UnitAttribute attribute, int value, UnitModifierType type = UnitModifierType.Value) => new()
    {
        Template = new BonusTemplate { Attribute = attribute, ModifierType = type }, Value = value
    };

    private sealed class RecordingCharacter : CharacterMock
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }
}
