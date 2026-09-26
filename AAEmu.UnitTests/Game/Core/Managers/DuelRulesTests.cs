using System.Reflection;
using System.Collections.Concurrent;
using AAEmu.Game.Models;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Duels;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class DuelRulesTests
{
    private readonly List<Action> _restore = [];
    private DuelManager _manager;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        _manager = new DuelManager();
        Install(_manager);
        var zones = new ZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<ITaskManager>().Object);
        typeof(ZoneManager).GetField("_zones", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(zones, new Dictionary<uint, Zone>());
        Install(zones);
        _world = new WorldInstance(null, 0, true, 0);
        var worlds = new WorldManager(null, null, null, null, null);
        typeof(WorldManager).GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(worlds, new ConcurrentDictionary<uint, WorldInstance>(new Dictionary<uint, WorldInstance> { [0] = _world }));
        Install(worlds);
        var previousWorldConfig = AppConfiguration.Instance.World;
        _restore.Add(() => AppConfiguration.Instance.World = previousWorldConfig);
        AppConfiguration.Instance.World = new WorldConfig { PvPDurabilityLossRate = 0, PvEDurabilityLossRate = 0 };

    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var restore in _restore) restore();
    }

    [Test]
    public async Task ForgedAcceptanceAndDecline_DoNotChangeTheChallenge()
    {
        var first = Player(1);
        var second = Player(2);
        var stranger = Player(3);
        await Assert.That(_manager.TryCreateDuel(first, second, 30, out var duel)).IsTrue();
        await Assert.That(_manager.TryAccept(stranger, first.Id, out _)).IsFalse();
        _manager.DuelCancel(stranger, first.Id, ErrorMessageType.TargetRejectedDuel);
        await Assert.That(_manager.IsCurrent(duel)).IsTrue();
        await Assert.That(duel.DuelStarted).IsFalse();
        await Assert.That(_manager.TryAccept(second, second.Id, out _)).IsFalse();
        await Assert.That(_manager.TryAccept(second, first.Id, out _)).IsTrue();
        await Assert.That(_manager.TryAccept(second, first.Id, out _)).IsFalse();
        _manager.DuelCancel(second, first.Id, ErrorMessageType.TargetRejectedDuel);
        await Assert.That(_manager.IsCurrent(duel)).IsTrue();
    }

    [Test]
    [Arguments("null")] [Arguments("self")] [Arguments("dead")] [Arguments("offline")]
    [Arguments("far")] [Arguments("instance")] [Arguments("nan")] [Arguments("force")]
    public async Task InvalidTarget_DoesNotReserveEitherPlayer(string scenario)
    {
        var first = Player(1);
        var second = Player(2);
        switch (scenario)
        {
            case "null": second = null; break;
            case "self": second = first; break;
            case "dead": second.Hp = 0; break;
            case "offline": typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(second, false); break;
            case "far": second.Transform.World.SetPosition(30.01f, 0, 0); break;
            case "instance": second.ParentWorld = new WorldInstance(null, 0, true, 99); break;
            case "nan": second.Transform.World.SetPosition(float.NaN, 0, 0); break;
            case "force": first.ForceAttack = true; break;
        }
        await Assert.That(_manager.TryCreateDuel(first, second, 30, out _)).IsFalse();
        await Assert.That(_manager.TryCreateDuel(Player(1), Player(2), 30, out _)).IsTrue();
    }

    [Test]
    public async Task Acceptance_RechecksDistanceAndReleasesTheFailedChallenge()
    {
        var first = Player(1);
        var second = Player(2);
        await Assert.That(_manager.TryCreateDuel(first, second, 30, out var duel)).IsTrue();
        second.Transform.World.SetPosition(31, 0, 0);
        await Assert.That(_manager.TryAccept(second, first.Id, out _)).IsFalse();
        await Assert.That(_manager.IsCurrent(duel)).IsFalse();
    }

    [Test]
    public async Task ConcurrentChallenges_ReserveAPlayerOnlyOnce()
    {
        var target = Player(1);
        var contenders = Enumerable.Range(2, 12).Select(value => Player((uint)value)).ToArray();
        var results = await Task.WhenAll(contenders.Select(player => Task.Run(() =>
            _manager.TryCreateDuel(player, target, 30, out _))));
        await Assert.That(results.Count(value => value)).IsEqualTo(1);
    }

    [Test]
    public async Task OldDuelCallbacks_CannotChangeTheNextDuel()
    {
        var first = Player(1);
        var second = Player(2);
        _manager.TryCreateDuel(first, second, 30, out var oldDuel);
        _manager.DuelCancel(second, first.Id, ErrorMessageType.TargetRejectedDuel);
        _manager.TryCreateDuel(first, second, 30, out var newDuel);
        _manager.DuelStart(oldDuel);
        _manager.EndTimer(oldDuel);
        _manager.CheckDistance(oldDuel);
        _manager.Stop(oldDuel, DuelDetType.Draw);
        await Assert.That(_manager.IsCurrent(newDuel)).IsTrue();
        await Assert.That(newDuel.DuelStarted).IsFalse();
    }

    [Test]
    public async Task ConcurrentDuels_DoNotChangeFactionsOrOtherPairsRelations()
    {
        var first = Player(1);
        var second = Player(2);
        var third = Player(3);
        var fourth = Player(4);
        var faction = first.Faction;
        second.Faction = third.Faction = fourth.Faction = faction;
        _manager.TryCreateDuel(first, second, 30, out var one);
        _manager.TryCreateDuel(third, fourth, 30, out var two);
        one.Active = two.Active = true;
        await Assert.That(first.GetRelationStateTo(second)).IsEqualTo(RelationState.Hostile);
        await Assert.That(first.GetRelationStateTo(third)).IsEqualTo(RelationState.Friendly);
        _manager.CancelForCharacter(first);
        await Assert.That(_manager.IsCurrent(two)).IsTrue();
        await Assert.That(first.Faction).IsEqualTo(faction);
        await Assert.That(second.Faction).IsEqualTo(faction);
        await Assert.That(first.GetRelationStateTo(second)).IsEqualTo(RelationState.Friendly);
    }

    [Test]
    [Arguments("opponent", false)] [Arguments("mate", false)] [Arguments("stranger", true)]
    [Arguments("npc", true)] [Arguments("fall", true)] [Arguments("gm", true)]
    public async Task LethalDamage_OnlyTheActiveOpponentIsNonlethal(string source, bool dies)
    {
        var first = Player(1);
        var second = Player(2);
        _manager.TryCreateDuel(first, second, 30, out var duel);
        duel.Active = true;
        BaseUnit attacker = source switch
        {
            "mate" => new Mate { OwnerObjId = first.ObjId, ParentWorld = _world },
            "stranger" => Player(3),
            "npc" => new Npc(),
            "fall" => second,
            _ => first
        };
        var reason = source == "fall" ? KillReason.Fall : source == "gm" ? KillReason.Gm : KillReason.Damage;
        second.ReduceCurrentHp(attacker, 200, reason);
        await Assert.That(second.Hp).IsEqualTo(dies ? 0 : 1);
        await Assert.That(second.Died).IsEqualTo(dies);
        await Assert.That(_manager.IsCurrent(duel)).IsFalse();
    }

    [Test]
    public async Task WinningDamageEffect_DoesNotCreateCrimeOrAssaultEvidence()
    {
        var first = Player(1);
        var second = Player(2);
        _manager.TryCreateDuel(first, second, 30, out var duel);
        duel.Active = true;
        var effect = new DamageEffect
        {
            DamageType = DamageType.Siege, UseFixedDamage = true, FixedMin = 200, FixedMax = 200, CheckCrime = true
        };
        effect.Apply(first, new SkillCasterUnit(first.ObjId), second, new SkillCastUnitTarget(second.ObjId),
            new CastSkill(10135, 0), new EffectSource(), null, DateTime.UtcNow);
        await Assert.That(second.Hp).IsEqualTo(1);
        await Assert.That(_manager.IsCurrent(duel)).IsFalse();
        await Assert.That(first.AssaultOn).IsEmpty();
        await Assert.That(second.AssaultedBy).IsEmpty();
        await Assert.That(first.GetRelationStateTo(second)).IsEqualTo(RelationState.Friendly);
    }

    [Test]
    public async Task WinningCast_DiscardsItsLaterHostileBuffAndDelayedDamage()
    {
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_skillReagents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillReagent>());
        typeof(SkillManager).GetField("_skillProducts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillProduct>());
        Install(skills);
        var first = Player(1);
        var second = Player(2);
        _manager.TryCreateDuel(first, second, 30, out var duel);
        duel.Active = true;
        var damage = new DamageEffect
        {
            Id = 87, DamageType = DamageType.Siege, UseFixedDamage = true,
            FixedMin = 200, FixedMax = 200, CheckCrime = true
        };
        var badBuff = new BuffTemplate { Id = 87, Kind = BuffKind.Bad };
        // Skill 10135 applies DamageEffect 87 before BuffEffect 639 (buff 87).
        // Fixed siege damage removes unrelated formula/dice setup from this test.
        var skill = new Skill(new SkillTemplate { Id = 10135, TargetType = SkillTargetType.AnyUnit });
        skill.Template.Effects.Add(Effect(damage));
        skill.Template.Effects.Add(Effect(new BuffEffect { Id = 639, Buff = badBuff, Chance = 100 }));
        var caster = new SkillCasterUnit(first.ObjId);
        var target = new SkillCastUnitTarget(second.ObjId);
        var lingeringBuff = new Buff(second, first, caster, badBuff, skill, DateTime.UtcNow);
        skill.ApplyEffects(first, caster, second, target, null);
        await Assert.That(second.Hp).IsEqualTo(1);
        await Assert.That(_manager.IsCurrent(duel)).IsFalse();
        await Assert.That(second.Buffs.CheckBuff(87)).IsFalse();
        await Assert.That(first.AssaultOn).IsEmpty();
        await Assert.That(second.AssaultedBy).IsEmpty();

        // Both plot effects (same Skill) and buff ticks retain the old duel.
        // They must not adopt a later duel between the same characters.
        second.Hp = 100;
        _manager.TryCreateDuel(first, second, 30, out var next);
        next.Active = true;
        damage.Apply(first, caster, second, target, new CastSkill(10135, 0), new EffectSource(skill), null, DateTime.UtcNow);
        damage.Apply(first, caster, second, target, new CastBuff(lingeringBuff),
            new EffectSource(badBuff) { DuelContext = lingeringBuff.DuelContext }, null, DateTime.UtcNow);
        await Assert.That(second.Hp).IsEqualTo(100);
        await Assert.That(_manager.IsCurrent(next)).IsTrue();

        static SkillEffect Effect(EffectTemplate template) => new()
        {
            EffectId = template.Id, Template = template, ApplicationMethod = SkillEffectApplicationMethod.Target,
            Friendly = true, NonFriendly = true, Front = true, Back = true, StartLevel = 0, EndLevel = 99, Chance = 100
        };
    }

    [Test]
    public async Task OldCast_CannotAdoptANewDuelAgainstADifferentOpponent()
    {
        var first = Player(1);
        var second = Player(2);
        var third = Player(3);
        _manager.TryCreateDuel(first, second, 30, out var old);
        old.Active = true;
        var source = new EffectSource(new Skill { DuelContext = old });
        _manager.Stop(old, DuelDetType.Draw);
        _manager.TryCreateDuel(first, third, 30, out var current);
        current.Active = true;
        using var nextOpponent = _manager.EnterEffect(first, third, source);
        using var oldOpponent = _manager.EnterEffect(first, second, source);
        using var npc = _manager.EnterEffect(first, new Npc(), source);
        using var bystander = _manager.EnterEffect(first, Player(4), source);
        await Assert.That(nextOpponent.Allowed).IsFalse();
        await Assert.That(oldOpponent.Allowed).IsFalse();
        await Assert.That(npc.Allowed).IsTrue();
        await Assert.That(bystander.Allowed).IsTrue();
        await Assert.That(_manager.IsCurrent(current)).IsTrue();
    }

    [Test]
    public async Task Result_WaitsForActiveEffectsWithoutHoldingTheManagerLock()
    {
        var first = Player(1);
        var second = Player(2);
        _manager.TryCreateDuel(first, second, 30, out var duel);
        duel.Active = true;
        var source = new EffectSource(new Skill());
        using (var active = _manager.EnterEffect(first, second, source))
        {
            second.ReduceCurrentHp(first, 200);
            await Assert.That(_manager.IsCurrent(duel)).IsTrue();
            await Assert.That(first.GetRelationStateTo(second)).IsEqualTo(RelationState.Hostile);
            using var late = _manager.EnterEffect(first, second, source);
            await Assert.That(late.Allowed).IsFalse();
            var other = await Task.Run(() => _manager.TryCreateDuel(Player(3), Player(4), 30, out _))
                .WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.That(other).IsTrue();
        }
        await Assert.That(_manager.IsCurrent(duel)).IsFalse();
        await Assert.That(first.GetRelationStateTo(second)).IsEqualTo(RelationState.Friendly);
    }

    [Test]
    public async Task FlagFactory_DoesNotHoldTheDuelLockWhileItWaitsForPersistence()
    {
        _manager.TryCreateDuel(Player(1), Player(2), 30, out var duel);
        _manager.TryAccept(duel.Challenged, duel.Challenger.Id, out _);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var spawn = Task.Run(() => _manager.CreateFlag(duel, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test flag factory did not receive its release");
            return null;
        }, () => { }));
        await Assert.That(entered.Wait(TimeSpan.FromSeconds(2))).IsTrue();
        try
        {
            var other = Task.Run(() => _manager.TryCreateDuel(Player(3), Player(4), 30, out _));
            await Assert.That(await other.WaitAsync(TimeSpan.FromSeconds(2))).IsTrue();
        }
        finally
        {
            release.Set();
            await spawn;
        }
    }

    [Test]
    public async Task FlagFactory_CancellationDeletesTheUnpublishedFlagWithoutChangingANewDuel()
    {
        var first = Player(1);
        var second = Player(2);
        _manager.TryCreateDuel(first, second, 30, out var old);
        _manager.TryAccept(second, first.Id, out _);
        Duel next = null;
        var flag = new TestFlag { ParentWorld = _world };
        var published = false;
        _manager.CreateFlag(old, () =>
        {
            _manager.CancelForCharacter(first);
            _manager.TryCreateDuel(first, second, 30, out next);
            return flag;
        }, () => published = true);
        await Assert.That(flag.Deleted).IsTrue();
        await Assert.That(published).IsFalse();
        await Assert.That(_manager.IsCurrent(next)).IsTrue();
        await Assert.That(first.IsInDuel).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(5)] [Arguments(6)] [Arguments(7)]
    public async Task ChallengePacket_RequiresExactlyFourBytes(int length)
    {
        await Assert.That(CSChallengeDuelPacket.TryReadRequest(new PacketStream(new byte[length]), out _)).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)] [Arguments(7)]
    public async Task ReplyPacket_RequiresExactlySixBytes(int length)
    {
        await Assert.That(CSStartDuelPacket.TryReadRequest(new PacketStream(new byte[length]), out _, out _)).IsFalse();
    }

    [Test]
    [Arguments((short)0, true)] [Arguments((short)507, true)] [Arguments((short)1, false)] [Arguments((short)-1, false)]
    public async Task ReplyPacket_OnlyAcceptsTheNativeReplyValues(short reply, bool accepted)
    {
        var stream = new PacketStream();
        stream.Write(0x12345678u);
        stream.Write(reply);
        stream.Pos = 0;
        await Assert.That(CSStartDuelPacket.TryReadRequest(stream, out var id, out var readReply)).IsEqualTo(accepted);
        if (accepted)
        {
            await Assert.That(id).IsEqualTo(0x12345678u);
            await Assert.That(readReply).IsEqualTo(reply);
            await Assert.That(stream.Pos).IsEqualTo(stream.Count);
        }
    }

    private TestCharacter Player(uint id)
    {
        var player = new TestCharacter
        {
            Id = id, ObjId = id, Hp = 100, ParentWorld = _world,
            Faction = new SystemFaction { Id = FactionsEnum.Friendly }
        };
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, true);
        var objects = (ConcurrentDictionary<uint, GameObject>)typeof(WorldInstance)
            .GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_world)!;
        objects[id] = player;
        return player;
    }

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        _restore.Add(() => field.SetValue(null, previous));
        field.SetValue(null, value);
    }

    private sealed class TestFlag : Doodad
    {
        public bool Deleted { get; private set; }
        public override void Delete() => Deleted = true;
    }

    private sealed class TestCharacter : CharacterMock
    {
        public override int Flexibility => 0;
        public override int BattleResist => 0;
        public override float IncomingDamageMul => 1;
        public bool Died { get; private set; }
        public override void DoDie(BaseUnit killer, KillReason killReason)
        {
            Died = true;
            DuelManager.Instance.CancelForCharacter(this);
        }
    }

}
