using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Skills;

[NotInParallel]
public sealed class AutoAttackCadenceTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly List<(uint Skill, uint Target)> _hits = [];
    private TaskManager _tasks;
    private DateTime _now;
    private RecordingCharacter _caster;
    private Unit _target;
    private Dictionary<uint, SkillTemplate> _templates;

    [Before(Test)]
    public void SetUp()
    {
        _now = DateTime.UtcNow;
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
        var skills = new SkillManager(null, null);
        _templates = new Dictionary<uint, SkillTemplate>();
        foreach (var id in new uint[] { 2, 3, 4 })
        {
            var template = new SkillTemplate
            {
                Id = id, SourceAlive = true, TargetAlive = true, TargetType = SkillTargetType.AnyUnit,
                MaxRange = id == 4 ? 30 : 3, AbilityId = AbilityType.General
            };
            template.Effects.Add(new SkillEffect
            {
                Template = new RecordingImpact(_hits), ApplicationMethod = SkillEffectApplicationMethod.Target,
                StartLevel = 0, EndLevel = 255, Chance = 100, Friendly = true, NonFriendly = true
            });
            _templates[id] = template;
        }
        typeof(SkillManager).GetField("_skills", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(skills, _templates);
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
        _caster = new RecordingCharacter { Id = 1, ObjId = 1, Hp = 100, Mp = 100, Level = 1, IsAutoAttack = true };
        _target = new Unit { ObjId = 2, Hp = 100 };
        _target.Transform.Local.SetPosition(1, 0, 0);
        var world = new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        }, 0, true, 0);
        AddToWorld(world, _caster);
        AddToWorld(world, _target);
        _caster.CurrentTarget = _target;
    }

    [After(Test)]
    public void TearDown()
    {
        _tasks.Stop();
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    public async Task DifferentWeaponSpeeds_ProduceIndependentHitsAndChooseTheEarlierHand()
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var start = _now;
        var task = Start();
        At(task, start.AddMilliseconds(1500));
        await Assert.That(_hits.ToArray()).IsEquivalentTo(new[] { (2u, 2u) });
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(2000));
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.ToArray()).IsEquivalentTo(new[] { (2u, 2u), (3u, 2u) });
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(3000));
        At(task, start.AddMilliseconds(3000));
        At(task, start.AddMilliseconds(4000));
        await Assert.That(_hits.Count(hit => hit.Skill == 2)).IsEqualTo(2);
        await Assert.That(_hits.Count(hit => hit.Skill == 3)).IsEqualTo(2);
    }

    [Test]
    public async Task FasterOffhand_FirstWakeDoesNotWaitForMainhand()
    {
        Equip(EquipmentItemSlot.Mainhand, 6000);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var task = Start();
        await Assert.That(task.RepeatInterval).IsEqualTo(TimeSpan.FromMilliseconds(2000));
        At(task, _now.AddMilliseconds(2000));
        await Assert.That(_hits.Single()).IsEqualTo((3u, 2u));
    }

    [Test]
    [Arguments(0, 4000d)]
    [Arguments(500, 2666d)]
    [Arguments(-600, 10000d)]
    [Arguments(9000, 800d)]
    public async Task OffhandInterval_DoublesTheBaseBeforeTheBoundedRawConversion(int bonus, double expected)
    {
        Equip(EquipmentItemSlot.Offhand, 2000);
        _caster.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, bonus));
        _caster.AddBonus(2, Modifier(UnitAttribute.RangedSpeedMul, 4000));
        _caster.AddBonus(3, Modifier(UnitAttribute.GlobalCooldownMul, 4000));
        await Assert.That(SkillManager.GetAttackDelay(_templates[3], _caster)).IsEqualTo(expected);
    }

    [Test]
    public async Task EqualDueTimes_FireOneSeparateAttackPerHand()
    {
        Equip(EquipmentItemSlot.Mainhand, 2000);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var task = Start();
        At(task, _now.AddMilliseconds(2000));
        await Assert.That(_hits.ToArray()).IsEquivalentTo(new[] { (2u, 2u), (3u, 2u) });
        await Assert.That(_caster.Packets.OfType<SCSkillFiredPacket>().Count()).IsEqualTo(2);
        task.Execute();
        await Assert.That(_hits.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LateCallback_EmitsAtMostOneHitPerHandAndStartsNewCycles()
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var task = Start();
        At(task, _now.AddMinutes(1));
        await Assert.That(_hits.Count).IsEqualTo(2);
        await Assert.That(task.TriggerTime).IsEqualTo(_now.AddMilliseconds(1500));
        task.Execute();
        await Assert.That(_hits.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("cast")]
    [Arguments("gcd")]
    [Arguments("range")]
    public async Task PausedAttack_ConsumesOnlyDueHandsAndResumesWithoutCatchUp(string pause)
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var start = _now;
        var task = Start();
        if (pause == "cast")
            _caster.SkillTask = new UseAutoAttackSkillTask(new Skill(_templates[4]), _caster);
        if (pause == "gcd")
            _caster.GlobalCooldown = _now.AddHours(1);
        if (pause == "range")
            _target.Transform.Local.SetPosition(20, 0, 0);
        At(task, start.AddMilliseconds(1500));
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.Count).IsEqualTo(0);
        _caster.SkillTask = null;
        _caster.GlobalCooldown = DateTime.MinValue;
        _target.Transform.Local.SetPosition(1, 0, 0);
        At(task, start.AddMilliseconds(3000));
        await Assert.That(_hits.Single()).IsEqualTo((2u, 2u));
        At(task, start.AddMilliseconds(4000));
        await Assert.That(_hits.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TargetChange_DoesNotResetEitherHand()
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var start = _now;
        var task = Start();
        At(task, start.AddMilliseconds(1500));
        var next = new Unit { ObjId = 3, Hp = 100 };
        next.Transform.Local.SetPosition(1, 0, 0);
        AddToWorld(_caster.ParentWorld, next);
        _caster.CurrentTarget = next;
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.ToArray()).IsEquivalentTo(new[] { (2u, 2u), (3u, 3u) });
    }

    [Test]
    public async Task WeaponRanges_KeepTheOtherHandEligible()
    {
        Equip(EquipmentItemSlot.Mainhand, 2000);
        Equip(EquipmentItemSlot.Offhand, 1000);
        ((WeaponTemplate)_caster.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand).Template)
            .HoldableTemplate.MaxRange = 3;
        ((WeaponTemplate)_caster.Equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand).Template)
            .HoldableTemplate.MaxRange = 1;
        _target.Transform.Local.SetPosition(2, 0, 0);
        var start = _now;
        var task = Start();
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.Single()).IsEqualTo((2u, 2u));
        _target.Transform.Local.SetPosition(1, 0, 0);
        At(task, start.AddMilliseconds(4000));
        await Assert.That(_hits.Count(hit => hit.Skill == 2)).IsEqualTo(2);
        await Assert.That(_hits.Count(hit => hit.Skill == 3)).IsEqualTo(1);
    }

    [Test]
    public async Task ShieldRemovalAndReequip_StartANewOffhandCycle()
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000, EquipmentItemSlotType.Shield);
        var start = _now;
        var task = Start();
        At(task, start.AddMilliseconds(1500));
        Equip(EquipmentItemSlot.Offhand, 1000);
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.All(hit => hit.Skill == 2)).IsTrue();
        At(task, start.AddMilliseconds(3000));
        At(task, start.AddMilliseconds(4000));
        await Assert.That(_hits.Count(hit => hit.Skill == 3)).IsEqualTo(1);
        _caster.Equipment.Items.RemoveAll(item => item.Slot == (int)EquipmentItemSlot.Offhand);
        At(task, start.AddMilliseconds(4500));
        Equip(EquipmentItemSlot.Offhand, 1000);
        At(task, start.AddMilliseconds(6000));
        await Assert.That(_hits.Count(hit => hit.Skill == 3)).IsEqualTo(1);
        At(task, start.AddMilliseconds(8000));
        await Assert.That(_hits.Count(hit => hit.Skill == 3)).IsEqualTo(2);
    }

    [Test]
    public async Task SpeedChangeAndRemoval_PreserveTheStartOfEachCurrentCycle()
    {
        Equip(EquipmentItemSlot.Mainhand, 3000);
        Equip(EquipmentItemSlot.Offhand, 2000);
        var start = _now;
        var task = Start();
        _caster.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, 1000));
        At(task, start.AddMilliseconds(1000));
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(1500));
        At(task, start.AddMilliseconds(1500));
        await Assert.That(_hits.Single()).IsEqualTo((2u, 2u));
        _caster.RemoveBonus(1, UnitAttribute.MeleeSpeedMul);
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.Count).IsEqualTo(1);
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(4000));
        At(task, start.AddMilliseconds(4000));
        await Assert.That(_hits.Last()).IsEqualTo((3u, 2u));
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(4500));
    }

    [Test]
    public async Task WeaponSpeedChange_ChangesOnlyThatHandsPendingDueTime()
    {
        Equip(EquipmentItemSlot.Mainhand, 3000);
        Equip(EquipmentItemSlot.Offhand, 2000);
        var start = _now;
        var task = Start();
        Equip(EquipmentItemSlot.Mainhand, 1000);
        At(task, start.AddMilliseconds(1000));
        await Assert.That(_hits.Single()).IsEqualTo((2u, 2u));
        Equip(EquipmentItemSlot.Mainhand, 3000);
        At(task, start.AddMilliseconds(2000));
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(4000));
        At(task, start.AddMilliseconds(4000));
        await Assert.That(_hits.Count(hit => hit.Skill == 2)).IsEqualTo(2);
        await Assert.That(_hits.Count(hit => hit.Skill == 3)).IsEqualTo(1);
    }

    [Test]
    public async Task DynamicSpeedChangeAndRemoval_RefreshBothIndependentCycles()
    {
        Equip(EquipmentItemSlot.Mainhand, 3000);
        Equip(EquipmentItemSlot.Offhand, 2000);
        var start = _now;
        var source = new Buff(_caster, _caster, new SkillCasterUnit(1), new BuffTemplate { Id = 1 }, null, DateTime.UtcNow)
        {
            Duration = 1000, StartTime = DateTime.UtcNow.AddSeconds(10)
        };
        _caster.AddDynamicBonus(1, new DynamicBonus
        {
            Template = new DynamicBonusTemplate
            {
                Attribute = UnitAttribute.MeleeSpeedMul, ModifierType = UnitModifierType.Value, FuncType = "LinearFunc"
            },
            SourceBuff = source, LinearFunc = new LinearFuncTemplate { StartValue = 0, EndValue = 1000 }
        });
        var task = Start();
        source.StartTime = DateTime.UtcNow.AddSeconds(-10);
        At(task, start.AddMilliseconds(1500));
        await Assert.That(_hits.Single()).IsEqualTo((2u, 2u));
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(2000));
        _caster.RemoveDynamicBonus(1, UnitAttribute.MeleeSpeedMul);
        At(task, start.AddMilliseconds(2000));
        await Assert.That(_hits.Count).IsEqualTo(1);
        await Assert.That(task.TriggerTime).IsEqualTo(start.AddMilliseconds(4000));
    }

    [Test]
    public async Task MainhandKillsTarget_DueOffhandDoesNotHitTheDeadTarget()
    {
        Equip(EquipmentItemSlot.Mainhand, 2000);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var task = Start(callback: () => _target.Hp = 0);
        At(task, _now.AddMilliseconds(2000));
        await Assert.That(_hits.Single()).IsEqualTo((2u, 2u));
        await Assert.That(_caster.AutoAttackTask).IsNull();
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(0);
    }

    [Test]
    public async Task OverlappingCallbacks_DoNotRunTheSameHandsTwice()
    {
        Equip(EquipmentItemSlot.Mainhand, 2000);
        Equip(EquipmentItemSlot.Offhand, 1000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = Start(callback: () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        _now = _now.AddMilliseconds(2000);
        var first = System.Threading.Tasks.Task.Run(task.Execute);
        try
        {
            await Assert.That(entered.Wait(TimeSpan.FromSeconds(10))).IsTrue();
            task.Execute();
            await Assert.That(_hits.Count).IsEqualTo(1);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.That(_hits.Count).IsEqualTo(2);
    }

    [Test]
    public async Task OffhandSkillCooldown_DoesNotDelayTheMainhand()
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var start = _now;
        var task = Start();
        _caster.Cooldowns.AddCooldown(3, 60000, 0);
        At(task, start.AddMilliseconds(1500));
        At(task, start.AddMilliseconds(2000));
        At(task, start.AddMilliseconds(3000));
        await Assert.That(_hits.Count).IsEqualTo(2);
        await Assert.That(_hits.All(hit => hit.Skill == 2)).IsTrue();
    }

    [Test]
    [Arguments("caster")]
    [Arguments("target")]
    [Arguments("cancel")]
    [Arguments("targetless")]
    public async Task InvalidState_StopsBothHands(string state)
    {
        Equip(EquipmentItemSlot.Mainhand, 1500);
        Equip(EquipmentItemSlot.Offhand, 1000);
        var task = Start();
        if (state == "caster") _caster.Hp = 0;
        if (state == "target") _target.Hp = 0;
        if (state == "cancel") task.Cancelled = true;
        if (state == "targetless") _caster.CurrentTarget = null;
        At(task, _now.AddMilliseconds(2000));
        await Assert.That(_hits.Count).IsEqualTo(0);
        await Assert.That(_caster.AutoAttackTask).IsNull();
        await Assert.That(_caster.IsAutoAttack).IsFalse();
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(0);
    }

    [Test]
    public async Task ModeChangeAndRestart_RetireTheOldTaskWithoutAStaleCallbackCancellingTheNewTask()
    {
        var first = Start();
        _caster.StartAutoSkill(new Skill(_templates[4]));
        var ranged = _caster.AutoAttackTask;
        await Assert.That(first.Cancelled).IsTrue();
        await Assert.That(ranged.Skill.Template.Id).IsEqualTo(4u);
        first.Execute();
        await Assert.That(_caster.AutoAttackTask).IsSameReferenceAs(ranged);
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(1);
        _caster.StopAutoSkill(_caster);
        await Assert.That(_caster.AutoAttackTask).IsNull();
        await Assert.That(_caster.IsAutoAttack).IsFalse();
        _caster.StartAutoSkill(new Skill(_templates[2]));
        var restarted = _caster.AutoAttackTask;
        ranged.Execute();
        await Assert.That(_caster.AutoAttackTask).IsSameReferenceAs(restarted);
        await Assert.That(_caster.IsAutoAttack).IsTrue();
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(1);
    }

    [Test]
    public async Task RejectedSchedule_DoesNotLeaveAnActiveAutoAttack()
    {
        _tasks.Stop();
        _caster.StartAutoSkill(new Skill(_templates[2]));
        await Assert.That(_caster.AutoAttackTask).IsNull();
        await Assert.That(_caster.IsAutoAttack).IsFalse();
        await Assert.That(_tasks.GetQueueCount()).IsEqualTo(0);
    }

    [Test]
    public async Task StopAutoSkill_DoesNotClearATaskInstalledByTheCancellationCallback()
    {
        var replacement = new UseAutoAttackSkillTask(new Skill(_templates[4]), _caster);
        var previous = new CancellationTask(new Skill(_templates[2]), () => _caster.AutoAttackTask = replacement);
        _caster.AutoAttackTask = previous;
        _tasks.Schedule(previous, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));
        _caster.StopAutoSkill(_caster);
        await Assert.That(previous.Cancelled).IsTrue();
        await Assert.That(_caster.AutoAttackTask).IsSameReferenceAs(replacement);
        await Assert.That(_caster.IsAutoAttack).IsTrue();
    }

    private UseAutoAttackSkillTask Start(uint skill = 2, Action callback = null)
    {
        var task = new UseAutoAttackSkillTask(new Skill(_templates[skill]) { Callback = callback }, _caster, () => _now);
        _caster.AutoAttackTask = task;
        task.Schedule();
        return task;
    }

    private void At(UseAutoAttackSkillTask task, DateTime time)
    {
        _now = time;
        task.Execute();
    }

    private void Equip(EquipmentItemSlot slot, int speed, EquipmentItemSlotType type = EquipmentItemSlotType.OneHanded)
    {
        _caster.Equipment.Items.RemoveAll(item => item.Slot == (int)slot);
        _caster.Equipment.Items.Add(new Weapon
        {
            Slot = (int)slot,
            Template = new WeaponTemplate { HoldableTemplate = new Holdable { Speed = speed, SlotTypeId = (uint)type } }
        });
    }

    private static void AddToWorld(WorldInstance world, Unit unit)
    {
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unit, world);
        world.AddObject(unit);
    }

    private void Replace<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous[field] = field.GetValue(null);
        field.SetValue(null, manager);
    }

    private static Bonus Modifier(UnitAttribute attribute, int value) => new()
    {
        Template = new BonusTemplate { Attribute = attribute, ModifierType = UnitModifierType.Value }, Value = value
    };

    private sealed class RecordingCharacter : CharacterMock
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }

    private sealed class CancellationTask(Skill skill, Action callback) : SkillTask(skill)
    {
        public override void Execute() { }
        public override void OnCancel() => callback();
    }

    private sealed class RecordingImpact(List<(uint Skill, uint Target)> hits) : EffectTemplate
    {
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null)
        {
            hits.Add((source.Skill.Template.Id, target.ObjId));
        }
    }
}
