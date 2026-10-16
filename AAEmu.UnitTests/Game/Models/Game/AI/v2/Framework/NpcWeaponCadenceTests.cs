using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Framework;

[NotInParallel]
public sealed class NpcWeaponCadenceTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private NpcWeaponCadence _cadence;
    private QuietNpc _npc;
    private ProbeBehavior _behavior;

    [Before(Test)]
    public void SetUp()
    {
        var items = new ItemManager(null, null, null, null, null, null);
        typeof(ItemManager).GetField("_holdables", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(items, new Dictionary<uint, Holdable> { [0] = new() { Id = 0, Speed = 1500 } });
        Replace(items);
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_skills", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillTemplate>
            {
                [2] = new() { Id = 2, MaxRange = 10, SourceAlive = false },
                [10431] = new() { Id = 10431, MaxRange = 10, SourceAlive = false }
            });
        Replace(skills);
        Replace(new WorldManager(null, null, null, null, null));
        var models = new ModelManager();
        var types = typeof(ModelManager).GetField("_modelTypes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        types.SetValue(models, Activator.CreateInstance(types.FieldType));
        Replace(models);
        var limits = new UnitAttributeLimitsGameData();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unit_attribute_limits(unit_attribute_id INTEGER, minimum INTEGER, maximum INTEGER); " +
                              "INSERT INTO unit_attribute_limits VALUES (54,-800,4000),(55,-800,4000)";
        command.ExecuteNonQuery();
        limits.Load(connection);
        Replace(limits);
        _npc = new QuietNpc { ObjId = 1, Hp = 100, Template = new NpcTemplate { Scale = 1, BaseSkillId = 2 } };
        var ai = new ProbeAi { Owner = _npc };
        _npc.Ai = ai;
        _cadence = ai.WeaponCadence;
        _behavior = new ProbeBehavior { Ai = ai };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    [Arguments(2u)]
    [Arguments(3u)]
    [Arguments(4u)]
    public async Task AcceptedAttack_UsesFistIntervalAndItsOwnDeadline(uint skillId)
    {
        await Assert.That(_cadence.CanUse(_npc, skillId, Start)).IsTrue();
        _cadence.RecordResult(skillId, Start, SkillResult.Success);
        await Assert.That(_cadence.CanUse(_npc, skillId, Start.AddMilliseconds(1499))).IsFalse();
        await Assert.That(_cadence.CanUse(_npc, skillId, Start.AddMilliseconds(1500))).IsTrue();
        await Assert.That(_cadence.CanUse(_npc, skillId == 4 ? 2u : 4u, Start)).IsTrue();
    }

    [Test]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, 500, 1000)]
    [Arguments(3u, UnitAttribute.MeleeSpeedMul, 500, 1000)]
    [Arguments(4u, UnitAttribute.RangedSpeedMul, 500, 1000)]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, -600, 3750)]
    [Arguments(2u, UnitAttribute.MeleeSpeedMul, -900, 7500)]
    [Arguments(4u, UnitAttribute.RangedSpeedMul, 9000, 300)]
    public async Task RawSpeed_UsesAuthoredBoundsAndCurrentAttribute(uint skillId, UnitAttribute attribute, int raw, int interval)
    {
        _cadence.RecordResult(skillId, Start, SkillResult.Success);
        _npc.AddBonus(1, Modifier(attribute, raw));
        _npc.AddBonus(2, Modifier(UnitAttribute.GlobalCooldownMul, 4000));
        _npc.AddBonus(3, Modifier(UnitAttribute.AttackAnimSpeedMul, -800));
        _npc.AddBonus(4, Modifier(UnitAttribute.TwohandSpeedMul, 4000));
        _npc.AddBonus(5, Modifier(attribute == UnitAttribute.MeleeSpeedMul ? UnitAttribute.RangedSpeedMul : UnitAttribute.MeleeSpeedMul, 4000));
        await Assert.That(_cadence.CanUse(_npc, skillId, Start.AddMilliseconds(interval - 1))).IsFalse();
        await Assert.That(_cadence.CanUse(_npc, skillId, Start.AddMilliseconds(interval))).IsTrue();
        _npc.RemoveBonus(1, attribute);
        await Assert.That(_cadence.CanUse(_npc, skillId, Start.AddMilliseconds(1499))).IsFalse();
        await Assert.That(_cadence.CanUse(_npc, skillId, Start.AddMilliseconds(1500))).IsTrue();
    }

    [Test]
    public async Task Equipment_DoesNotReplaceTheNativeNpcFistSpec()
    {
        _npc.Equipment.Items.Add(new Weapon
        {
            Slot = (int)EquipmentItemSlot.Mainhand,
            Template = new WeaponTemplate { HoldableTemplate = new Holdable { Speed = 5000 } }
        });
        _cadence.RecordResult(2, Start, SkillResult.Success);
        await Assert.That(_cadence.CanUse(_npc, 2, Start.AddMilliseconds(1500))).IsTrue();
    }

    [Test]
    public async Task DynamicSpeed_ChangesThePendingIntervalAndRemovalRestoresIt()
    {
        var buff = new Buff(_npc, _npc, new SkillCasterUnit(1), new BuffTemplate { Id = 1 }, null, DateTime.UtcNow)
        { Duration = 1000, StartTime = DateTime.UtcNow.AddSeconds(10) };
        _npc.AddDynamicBonus(1, new DynamicBonus
        {
            Template = new DynamicBonusTemplate { Attribute = UnitAttribute.MeleeSpeedMul, ModifierType = UnitModifierType.Value, FuncType = "LinearFunc" },
            SourceBuff = buff, LinearFunc = new LinearFuncTemplate { StartValue = 0, EndValue = 500 }
        });
        _cadence.RecordResult(2, Start, SkillResult.Success);
        await Assert.That(_cadence.CanUse(_npc, 2, Start.AddMilliseconds(1000))).IsFalse();
        buff.StartTime = DateTime.UtcNow.AddSeconds(-10);
        await Assert.That(_cadence.CanUse(_npc, 2, Start.AddMilliseconds(1000))).IsTrue();
        _npc.RemoveDynamicBonus(1, UnitAttribute.MeleeSpeedMul);
        await Assert.That(_cadence.CanUse(_npc, 2, Start.AddMilliseconds(1000))).IsFalse();
    }

    [Test]
    [Arguments(SkillResult.CooldownTime)]
    [Arguments(SkillResult.OnCasting)]
    [Arguments(SkillResult.TooFarRange)]
    [Arguments(SkillResult.SourceDied)]
    [Arguments(SkillResult.LackMana)]
    public async Task RejectedAttack_DoesNotStartOrPostponeTheInterval(SkillResult result)
    {
        _cadence.RecordResult(2, Start, result);
        await Assert.That(_cadence.CanUse(_npc, 2, Start)).IsTrue();
        _cadence.RecordResult(2, Start, SkillResult.Success);
        _cadence.RecordResult(2, Start.AddMilliseconds(1000), result);
        await Assert.That(_cadence.CanUse(_npc, 2, Start.AddMilliseconds(1500))).IsTrue();
    }

    [Test]
    [Arguments(10431u)]
    [Arguments(15926u)]
    [Arguments(21562u)]
    [Arguments(11051u)]
    [Arguments(14268u)]
    public async Task OtherSkills_KeepTheirAuthoredTimingEvenWhenTheyAreTheBaseSkill(uint skillId)
    {
        _npc.Template.BaseSkillId = (int)skillId;
        _cadence.RecordResult(skillId, Start, SkillResult.Success);
        await Assert.That(NpcWeaponCadence.IsBasicAttack(skillId)).IsFalse();
        await Assert.That(_cadence.CanUse(_npc, skillId, Start)).IsTrue();
    }

    [Test]
    public async Task Reset_DiscardsThePreviousCombatDeadline()
    {
        _cadence.RecordResult(2, Start, SkillResult.Success);
        _cadence.RecordResult(4, Start, SkillResult.Success);
        _cadence.Reset();
        await Assert.That(_cadence.CanUse(_npc, 2, Start)).IsTrue();
        await Assert.That(_cadence.CanUse(_npc, 4, Start)).IsTrue();
    }

    [Test]
    public async Task SharedBehavior_RejectsAnEarlyBasicAttackBeforeTheSkillStarts()
    {
        _cadence.RecordResult(2, DateTime.UtcNow, SkillResult.Success);
        var skill = new Skill(new SkillTemplate { Id = 2, MaxRange = 10 });
        await Assert.That(_behavior.UseSkill(skill, _npc)).IsEqualTo(SkillResult.CooldownTime);
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task SharedBehavior_FailedCastDoesNotConsumeTheWeaponInterval()
    {
        // The living NPC rejects SourceAlive=false before any skill side effects.
        var skill = new Skill(new SkillTemplate { Id = 2, MaxRange = 10, SourceAlive = false });
        await Assert.That(_behavior.UseSkill(skill, _npc)).IsEqualTo(SkillResult.SourceAlive);
        await Assert.That(_cadence.CanUse(_npc, 2, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task PostSkillDelay_RemainsIndependentOfWeaponSpeed()
    {
        _npc.AddBonus(1, Modifier(UnitAttribute.MeleeSpeedMul, 4000));
        _behavior.SetPostSkillDelay(2);
        var before = DateTime.UtcNow;
        _behavior.OnSkillEnded();
        await Assert.That(_behavior.DelayEnd >= before.AddSeconds(2)).IsTrue();
        await Assert.That(_behavior.DelayEnd <= DateTime.UtcNow.AddSeconds(2)).IsTrue();
    }

    [Test]
    public async Task BasicAttack_ZeroAuthoredDelayDoesNotUseTheOldRandomThrottle()
    {
        var first = _behavior.PickSkillAndUseIt(SkillUseConditionKind.InCombat, _npc, 0);
        var second = _behavior.PickSkillAndUseIt(SkillUseConditionKind.InCombat, _npc, 0);
        await Assert.That(first).IsEqualTo(SkillResult.SourceAlive);
        await Assert.That(second).IsEqualTo(SkillResult.SourceAlive);
    }

    [Test]
    public async Task BasicAttack_PositiveAuthoredDelayKeepsItsThrottle()
    {
        _npc.Template.BaseSkillDelay = 60;
        var first = _behavior.PickSkillAndUseIt(SkillUseConditionKind.InCombat, _npc, 0);
        var second = _behavior.PickSkillAndUseIt(SkillUseConditionKind.InCombat, _npc, 0);
        await Assert.That(first).IsEqualTo(SkillResult.SourceAlive);
        await Assert.That(second).IsEqualTo(SkillResult.InvalidSkill);
    }

    [Test]
    public async Task OrdinaryBowSkill_KeepsTheOldFallbackThrottle()
    {
        _npc.Template.BaseSkillId = 10431;
        var first = _behavior.PickSkillAndUseIt(SkillUseConditionKind.InCombat, _npc, 0);
        var second = _behavior.PickSkillAndUseIt(SkillUseConditionKind.InCombat, _npc, 0);
        await Assert.That(first).IsEqualTo(SkillResult.SourceAlive);
        await Assert.That(second).IsEqualTo(SkillResult.InvalidSkill);
    }

    [Test]
    [Arguments(BehaviorKind.Spawning)]
    [Arguments(BehaviorKind.ReturnState)]
    [Arguments(BehaviorKind.Dead)]
    [Arguments(BehaviorKind.Despawning)]
    [Arguments(BehaviorKind.DoNothing)]
    [Arguments(BehaviorKind.Dummy)]
    public async Task LifecycleTransition_ResetsThePreviousCombatInterval(BehaviorKind kind)
    {
        var ai = (ProbeAi)_npc.Ai;
        ai.Start();
        ai.Change(BehaviorKind.Attack);
        _cadence.RecordResult(2, Start, SkillResult.Success);
        ai.Change(kind);
        await Assert.That(_cadence.CanUse(_npc, 2, Start)).IsTrue();
    }

    [Test]
    public async Task ExactCompact_UsesFistAndKeepsOrdinaryNpcBowSkillsOutsideTheAutoAttackGate()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT speed FROM holdables WHERE id=0 AND name='fist'";
        await Assert.That(Convert.ToInt32(command.ExecuteScalar())).IsEqualTo(1500);
        command.CommandText = "SELECT COUNT(*) FROM npcs WHERE base_skill_id=4";
        await Assert.That(Convert.ToInt32(command.ExecuteScalar())).IsEqualTo(4);
        command.CommandText = "SELECT cooldown_time FROM skills WHERE id=10431 AND weapon_slot_for_autoattack_id=0";
        await Assert.That(Convert.ToInt32(command.ExecuteScalar())).IsEqualTo(2000);
        command.CommandText = "SELECT cooldown_time FROM skills WHERE id=11051 AND weapon_slot_for_autoattack_id=15";
        await Assert.That(Convert.ToInt32(command.ExecuteScalar())).IsEqualTo(15000);
    }

    private void Replace<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, instance);
    }

    private static Bonus Modifier(UnitAttribute attribute, int value) => new()
    {
        Template = new BonusTemplate { Attribute = attribute, ModifierType = UnitModifierType.Value }, Value = value
    };

    private sealed class ProbeAi : NpcAi
    {
        internal void Change(BehaviorKind kind) => SetCurrentBehavior(kind);

        protected override void Build()
        {
            AddBehavior(BehaviorKind.Attack, new ProbeBehavior());
            AddBehavior(BehaviorKind.Spawning, new SpawningBehavior());
            AddBehavior(BehaviorKind.ReturnState, new QuietReturn());
            AddBehavior(BehaviorKind.Dead, new QuietDead());
            AddBehavior(BehaviorKind.Despawning, new QuietDespawn());
            AddBehavior(BehaviorKind.DoNothing, new DoNothingBehavior());
            AddBehavior(BehaviorKind.Dummy, new DummyBehavior());
        }
    }

    private sealed class QuietReturn : ReturnStateBehavior
    {
        public override void Enter() { }
    }

    private sealed class QuietDead : DeadBehavior
    {
        public override void Enter() { }
    }

    private sealed class QuietDespawn : DespawningBehavior
    {
        public override void Enter() { }
    }

    private sealed class ProbeBehavior : Behavior
    {
        internal DateTime DelayEnd => _delayEnd;
        internal void SetPostSkillDelay(float delay) => _nextTimeToDelay = delay;
        public override void Enter() { }
        public override void Tick(TimeSpan delta) { }
        public override void Exit() { }
    }

    private sealed class QuietNpc : Npc
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }
}
