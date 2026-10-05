using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

[NotInParallel]
public sealed partial class BuffSpecialEffectsTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private Dictionary<uint, BuffTemplate> _templates;
    private ProbeUnit _thief;
    private ProbeUnit _victim;

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        BuffBreakerTestData.Configure(skills);
        _templates = (Dictionary<uint, BuffTemplate>)typeof(SkillManager)
            .GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(skills)!;
        // Real r208022 IDs and duration/charge data. Stat modifiers are not needed here.
        _templates[11] = new() { Id = 11, Kind = BuffKind.Good, Duration = 180000, StackRule = BuffStackRule.Refresh };
        _templates[69] = new() { Id = 69, Kind = BuffKind.Good, Duration = 60000, InitMinCharge = 20, InitMaxCharge = 20 };
        _templates[34] = new() { Id = 34, Kind = BuffKind.Good };
        _templates[9002] = new() { Id = 9002, Kind = BuffKind.Good, System = true };
        _templates[9003] = new() { Id = 9003, Kind = BuffKind.Bad };
        _templates[9004] = new() { Id = 9004, Kind = BuffKind.Hidden };
        _templates[127] = new() { Id = 127, Kind = BuffKind.Good, Duration = 15000, StackRule = BuffStackRule.Extend, MaxStack = 3 };
        _templates[449] = new() { Id = 449, Kind = BuffKind.Bad, Duration = 1500 };
        typeof(SkillManager).GetField("_skillReagents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillReagent>());
        typeof(SkillManager).GetField("_skillProducts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillProduct>());
        SetInstance(skills);
        SetInstance(BuffBreakerTestData.Load());
        SetInstance(new DuelManager());
        SetInstance(new UnitAttributeLimitsGameData());
        SetInstance(new AAEmu.Game.Core.Managers.World.WorldManager(null, null, null, null, null));
        var zones = new AAEmu.Game.Core.Managers.World.ZoneManager(null, null);
        typeof(AAEmu.Game.Core.Managers.World.ZoneManager).GetField("_zones", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(zones, new Dictionary<uint, AAEmu.Game.Models.Game.World.Zones.Zone>());
        SetInstance(zones);
        SetInstance(new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        _thief = new ProbeUnit { ObjId = 100, Id = 10, Hp = 100, Level = 50 };
        _victim = new ProbeUnit { ObjId = 200, Id = 20, Hp = 100, Level = 30 };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    public async Task Leech_TransfersOneBuffWithRemainingDurationLevelAndCharges()
    {
        var now = DateTime.UtcNow;
        var original = Add(69, now.AddSeconds(-20));
        original.AbLevel = 37;
        original.Charge = 7;
        Add(11, now);
        _victim.Packets.Clear();

        ApplyLeech(now);

        var stolen = _thief.Buffs.GetEffectFromBuffId(69);
        await Assert.That(stolen).IsNotNull();
        await Assert.That(stolen.Duration).IsEqualTo(40000);
        await Assert.That(stolen.AbLevel).IsEqualTo(37u);
        await Assert.That(stolen.Charge).IsEqualTo(7);
        await Assert.That(stolen.Caster).IsSameReferenceAs(_thief);
        await Assert.That(_victim.Buffs.CheckBuff(69)).IsFalse();
        await Assert.That(_victim.Buffs.CheckBuff(11)).IsTrue();
        await Assert.That(original.State).IsEqualTo(EffectState.Finished);
        var removed = _victim.Packets.OfType<SCBuffRemovedPacket>().Single();
        var removedBody = new PacketStream();
        removed.Write(removedBody);
        await Assert.That(removedBody.ReadBc()).IsEqualTo(_victim.ObjId);
        await Assert.That(removedBody.ReadUInt32()).IsEqualTo(original.Index);
        await Assert.That(removedBody.LeftBytes).IsEqualTo(0);
        var createdBody = new PacketStream();
        _thief.Packets.OfType<SCBuffCreatedPacket>().Single().Write(createdBody);
        await Assert.That(createdBody.ReadByte()).IsEqualTo((byte)SkillCasterType.Unit);
        await Assert.That(createdBody.ReadBc()).IsEqualTo(_thief.ObjId);
        await Assert.That(createdBody.ReadUInt32()).IsEqualTo(_thief.Id);
        await Assert.That(createdBody.ReadBc()).IsEqualTo(_thief.ObjId);
        await Assert.That(createdBody.ReadUInt32()).IsEqualTo(stolen.Index);
        await Assert.That(createdBody.ReadUInt32()).IsEqualTo(69u);
        await Assert.That(createdBody.ReadByte()).IsEqualTo(_thief.Level);
        await Assert.That(createdBody.ReadInt16()).IsEqualTo((short)37);
        await Assert.That(createdBody.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(createdBody.ReadPiscW(4).SequenceEqual([7L, 4000L, 0L, 0L])).IsTrue();
        await Assert.That(createdBody.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Leech_IgnoresPassiveSystemBadHiddenAndExpiredBuffs()
    {
        var now = DateTime.UtcNow;
        var passive = Add(34, now);
        passive.Passive = true;
        Add(9002, now);
        Add(9003, now);
        Add(9004, now);
        var expired = Add(11, now.AddMinutes(-4));
        Add(69, now);

        ApplyLeech(now);

        await Assert.That(_thief.Buffs.CheckBuff(69)).IsTrue();
        await Assert.That(_victim.Buffs.CheckBuff(34)).IsTrue();
        await Assert.That(_victim.Buffs.CheckBuff(9002)).IsTrue();
        await Assert.That(_victim.Buffs.CheckBuff(9003)).IsTrue();
        await Assert.That(_victim.Buffs.CheckBuff(9004)).IsTrue();
        await Assert.That(expired.InUse).IsTrue();
    }

    [Test]
    public async Task Leech_PermanentBuffRemainsPermanentAndCannotBeStolenTwice()
    {
        Add(34, DateTime.UtcNow);
        ApplyLeech(DateTime.UtcNow);
        ApplyLeech(DateTime.UtcNow);

        await Assert.That(_thief.Buffs.GetEffectFromBuffId(34).Duration).IsEqualTo(0);
        await Assert.That(_thief.Packets.OfType<SCBuffCreatedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Leech_DeadVictimOrMissDoesNotRemoveBuff()
    {
        Add(34, DateTime.UtcNow);
        _victim.Hp = 0;
        ApplyLeech(DateTime.UtcNow);
        _victim.Hp = 100;
        var skill = new Skill(new SkillTemplate { Id = 23707 }, _thief);
        skill.HitTypes[_victim.ObjId] = SkillHitType.SpellMiss;
        ApplyLeech(DateTime.UtcNow, skill);

        await Assert.That(_victim.Buffs.CheckBuff(34)).IsTrue();
        await Assert.That(_thief.Packets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Leech_ExistingLongerBuffKeepsItsDurationWhileEnemyLosesCopy()
    {
        var now = DateTime.UtcNow;
        var existing = new Buff(_thief, _thief, new SkillCasterUnit(_thief.ObjId), _templates[11], null, now);
        _thief.Buffs.AddBuff(existing);
        _thief.Packets.Clear();
        Add(11, now.AddSeconds(-120));

        ApplyLeech(now);

        await Assert.That(_victim.Buffs.CheckBuff(11)).IsFalse();
        await Assert.That(_thief.Buffs.GetEffectFromBuffId(11)).IsSameReferenceAs(existing);
        await Assert.That(existing.Duration).IsEqualTo(180000);
        await Assert.That(_thief.Packets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Leech_RequiredCompanionBuffMissingDoesNotConsumeTargetBuff()
    {
        _templates[9005] = new() { Id = 9005, Kind = BuffKind.Good, RequireBuffId = 9004 };
        Add(9005, DateTime.UtcNow);

        ApplyLeech(DateTime.UtcNow);

        await Assert.That(_victim.Buffs.CheckBuff(9005)).IsTrue();
        await Assert.That(_thief.Packets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConsumeBuff_ConcurrentAndReentrantConsumersOnlySucceedOnce()
    {
        var buff = Add(34, DateTime.UtcNow);
        var callbackConsumed = false;
        var callbacks = 0;
        buff.Events.OnTimeout += (_, _) =>
        {
            callbacks++;
            callbackConsumed = _victim.Buffs.TryConsumeActiveBuff(buff);
        };
        var consumed = 0;

        Parallel.For(0, 32, _ =>
        {
            if (_victim.Buffs.TryConsumeActiveBuff(buff))
                Interlocked.Increment(ref consumed);
        });

        await Assert.That(consumed).IsEqualTo(1);
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(callbackConsumed).IsFalse();
        await Assert.That(_victim.Buffs.CheckBuff(34)).IsFalse();
    }

    [Test]
    public async Task ConsumeBuff_DoesNotHoldCollectionLockWhileExitWaitsForBuffLock()
    {
        var buff = Add(34, DateTime.UtcNow);
        var buffLock = typeof(Buff).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(buff)!;
        var collectionLock = typeof(AAEmu.Game.Models.Game.Units.Buffs)
            .GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_victim.Buffs)!;
        Task<bool> consume;
        var enteredExit = false;
        var enteredCollection = false;
        Monitor.Enter(buffLock);
        try
        {
            consume = Task.Run(() => _victim.Buffs.TryConsumeActiveBuff(buff));
            // Exit sets Finished before StopEffectTask takes Buff's own lock.
            // Keep that lock until this thread also tries the collection lock.
            enteredExit = SpinWait.SpinUntil(() => buff.State == EffectState.Finished, TimeSpan.FromSeconds(5));
            if (enteredExit)
            {
                enteredCollection = Monitor.TryEnter(collectionLock, TimeSpan.FromSeconds(1));
                if (enteredCollection)
                    Monitor.Exit(collectionLock);
            }
        }
        finally
        {
            Monitor.Exit(buffLock);
        }

        var consumed = await consume.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(enteredExit).IsTrue();
        await Assert.That(enteredCollection).IsTrue();
        await Assert.That(consumed).IsTrue();
        await Assert.That(_victim.Buffs.CheckBuff(34)).IsFalse();
    }

    [Test]
    public async Task ConsumeBuff_ObservedExpiryDoesNotConsumeOrRunCallbacks()
    {
        var buff = Add(11, DateTime.UtcNow.AddMinutes(-4));
        var callbacks = 0;
        buff.Events.OnTimeout += (_, _) => callbacks++;

        var consumed = _victim.Buffs.TryConsumeActiveBuff(buff);

        await Assert.That(consumed).IsFalse();
        await Assert.That(callbacks).IsEqualTo(0);
    }

    [Test]
    public async Task SkillMissed_TreatsSpellMissAndResistAsFailuresButSpellHitsAsSuccess()
    {
        var skill = new Skill(new SkillTemplate { Id = 23707 }, _thief);
        foreach (var result in new[] { SkillHitType.SpellMiss, SkillHitType.SpellResist, SkillHitType.Immune })
        {
            skill.HitTypes[_victim.ObjId] = result;
            await Assert.That(skill.SkillMissed(_victim.ObjId)).IsTrue();
        }
        foreach (var result in new[] { SkillHitType.SpellHit, SkillHitType.SpellCritical })
        {
            skill.HitTypes[_victim.ObjId] = result;
            await Assert.That(skill.SkillMissed(_victim.ObjId)).IsFalse();
        }
    }

    private Buff Add(uint id, DateTime start)
    {
        var buff = new Buff(_victim, _victim, new SkillCasterUnit(_victim.ObjId), _templates[id], null, start);
        _victim.Buffs.AddBuff(buff);
        return buff;
    }

    private void ApplyLeech(DateTime time, Skill skill = null) => new BuffSteal().Execute(_thief,
        new SkillCasterUnit(_thief.ObjId), _victim, new SkillCastUnitTarget(_victim.ObjId),
        new CastSkill(23707, 1), skill, new SkillObject(), time, 1, 0, 0, 0);

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private sealed class ProbeUnit : Unit
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
        public override void ReduceCurrentHp(BaseUnit attacker, int value,
            AAEmu.Game.Models.Game.Units.Static.KillReason killReason = AAEmu.Game.Models.Game.Units.Static.KillReason.Damage)
        {
            Hp -= value;
        }
    }
}
