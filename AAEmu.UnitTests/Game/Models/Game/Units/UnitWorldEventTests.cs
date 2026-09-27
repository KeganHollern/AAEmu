using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Archer;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.BigMonster;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Flytrap;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.WildBoar;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Indun;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class UnitWorldEventTests
{
    private FieldInfo _worldManagerField;
    private object _previousWorldManager;
    private readonly ConcurrentDictionary<uint, WorldInstance> _worlds = [];

    [Before(Test)]
    public void SetUp()
    {
        var manager = new WorldManager(null, null, null, null, null);
        typeof(WorldManager).GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, _worlds);
        _worldManagerField = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousWorldManager = _worldManagerField.GetValue(null);
        _worldManagerField.SetValue(null, manager);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var world in _worlds.Values)
            GC.SuppressFinalize(world);
        _worlds.Clear();
        _worldManagerField.SetValue(null, _previousWorldManager);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReduceCurrentHp_LethalDamage_RaisesEachDeathEventOnce(bool dungeon)
    {
        var world = CreateWorld(dungeon);
        var victim = CreateUnit(world, 1);
        var killer = CreateUnit(world, 2);
        var worldKills = new List<OnUnitKilledArgs>();
        var deaths = new List<OnDeathArgs>();
        var kills = new List<OnKillArgs>();
        world.Events.OnUnitKilled += (_, args) => worldKills.Add(args);
        victim.Events.OnDeath += (_, args) => deaths.Add(args);
        killer.Events.OnKill += (_, args) => kills.Add(args);

        victim.ReduceCurrentHp(killer, 40);
        await Assert.That(worldKills.Count).IsEqualTo(0);
        victim.ReduceCurrentHp(killer, 60);
        victim.ReduceCurrentHp(killer, 100);

        await Assert.That(victim.Hp).IsEqualTo(0);
        await Assert.That(worldKills.Count).IsEqualTo(1);
        await Assert.That(deaths.Count).IsEqualTo(1);
        await Assert.That(kills.Count).IsEqualTo(1);
        await Assert.That(worldKills[0].Killer).IsSameReferenceAs(killer);
        await Assert.That(worldKills[0].Victim).IsSameReferenceAs(victim);
        await Assert.That(deaths[0].Victim).IsSameReferenceAs(victim);
        await Assert.That(kills[0].Killer).IsSameReferenceAs(killer);
        await Assert.That(kills[0].Victim).IsSameReferenceAs(victim);
        await Assert.That(kills[0].Target).IsSameReferenceAs(victim);
        await Assert.That(killer.Packets.OfType<SCUnitDeathPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task DoDie_RepeatedAndReentrantCallbacks_RaiseDeathOnce()
    {
        var world = CreateWorld(true);
        var victim = CreateUnit(world, 1);
        var kills = 0;
        world.Events.OnUnitKilled += (_, _) => kills++;
        victim.Events.OnDeath += (_, _) => victim.DoDie(victim, KillReason.Damage);
        victim.Hp = 0;

        victim.DoDie(victim, KillReason.Damage);
        victim.DoDie(victim, KillReason.Damage);

        await Assert.That(kills).IsEqualTo(1);
        await Assert.That(victim.Packets.OfType<SCUnitDeathPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ReduceCurrentHp_Resurrection_AllowsOneDeathForEachLife()
    {
        var world = CreateWorld(true);
        var victim = CreateUnit(world, 1);
        var kills = 0;
        world.Events.OnUnitKilled += (_, _) => kills++;

        victim.ReduceCurrentHp(victim, 100);
        victim.Hp = 100;
        victim.ReduceCurrentHp(victim, 100);
        victim.DoDie(victim, KillReason.Damage);

        await Assert.That(kills).IsEqualTo(2);
        await Assert.That(victim.Packets.OfType<SCUnitDeathPacket>().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task PostUpdateCurrentHp_DeadUnitOrPendingRestoration_DoesNotCauseDeath()
    {
        var world = CreateWorld(true);
        var victim = CreateUnit(world, 1);
        var kills = 0;
        world.Events.OnUnitKilled += (_, _) => kills++;
        victim.Hp = 0;

        victim.PostUpdateCurrentHp(victim, 0, 0);
        victim.PostUpdateCurrentHp(victim, 0, 100);

        await Assert.That(kills).IsEqualTo(0);
    }

    [Test]
    public async Task IsInBattle_RepeatedAssignments_RaiseOnlyActualTransitions()
    {
        var world = CreateWorld(true);
        var unit = CreateUnit(world, 1);
        var starts = new List<OnUnitCombatStartArgs>();
        var ends = new List<OnUnitCombatEndArgs>();
        world.Events.OnUnitCombatStart += (_, args) => starts.Add(args);
        world.Events.OnUnitCombatEnd += (_, args) => ends.Add(args);

        unit.IsInBattle = false;
        unit.IsInBattle = true;
        unit.IsInBattle = true;
        unit.IsInBattle = false;
        unit.IsInBattle = false;

        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(ends.Count).IsEqualTo(1);
        await Assert.That(starts[0].Npc).IsSameReferenceAs(unit);
        await Assert.That(ends[0].Npc).IsSameReferenceAs(unit);
        await Assert.That(unit.Packets.OfType<SCCombatClearedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task IsInBattle_ConcurrentRepeatedAssignments_RaiseOneEventForEachTransition()
    {
        var world = CreateWorld(true);
        var unit = CreateUnit(world, 1);
        var starts = 0;
        var ends = 0;
        world.Events.OnUnitCombatStart += (_, _) => Interlocked.Increment(ref starts);
        world.Events.OnUnitCombatEnd += (_, _) => Interlocked.Increment(ref ends);

        for (var combat = 0; combat < 100; combat++)
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => unit.IsInBattle = true)));
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => unit.IsInBattle = false)));
        }

        await Assert.That(starts).IsEqualTo(100);
        await Assert.That(ends).IsEqualTo(100);
        await Assert.That(unit.Packets.OfType<SCCombatClearedPacket>().Count()).IsEqualTo(100);
    }

    [Test]
    public async Task CombatTick_TimeoutAndReentry_RaiseOneEndForEachCombat()
    {
        var world = CreateWorld(true);
        var unit = CreateUnit(world, 1);
        var starts = 0;
        var ends = 0;
        world.Events.OnUnitCombatStart += (_, _) => starts++;
        world.Events.OnUnitCombatEnd += (_, _) => ends++;

        for (var combat = 0; combat < 2; combat++)
        {
            unit.IsInBattle = true;
            unit.OnActiveRegionTick(TimeSpan.FromSeconds(1));
            await Assert.That(unit.IsInBattle).IsTrue();
            unit.LastCombatActivity = DateTime.UtcNow.AddSeconds(-WorldManager.DefaultCombatTimeout - 1);
            unit.OnActiveRegionTick(TimeSpan.FromSeconds(1));
            unit.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        }

        await Assert.That(starts).IsEqualTo(2);
        await Assert.That(ends).IsEqualTo(2);
        await Assert.That(unit.IsInBattle).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReduceCurrentHp_LethalDamage_EndsOnlyAnActiveCombat(bool inCombat)
    {
        var world = CreateWorld(true);
        var victim = CreateUnit(world, 1);
        var starts = 0;
        var ends = 0;
        var kills = 0;
        world.Events.OnUnitCombatStart += (_, _) => starts++;
        world.Events.OnUnitCombatEnd += (_, _) => ends++;
        world.Events.OnUnitKilled += (_, _) => kills++;
        victim.IsInBattle = inCombat;

        victim.ReduceCurrentHp(victim, 100);
        victim.ReduceCurrentHp(victim, 100);
        victim.IsInBattle = false;

        await Assert.That(starts).IsEqualTo(inCombat ? 1 : 0);
        await Assert.That(ends).IsEqualTo(inCombat ? 1 : 0);
        await Assert.That(kills).IsEqualTo(1);
        await Assert.That(victim.IsInBattle).IsFalse();
    }

    [Test]
    public async Task IsInBattle_UnitWithoutWorld_ChangesStateWithoutWorldEvents()
    {
        var unit = new RecordingUnit { Hp = 100 };
        unit.IsInBattle = true;
        unit.IsInBattle = false;

        await Assert.That(unit.IsInBattle).IsFalse();
        await Assert.That(unit.Packets.OfType<SCCombatClearedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments("common")]
    [Arguments("almighty")]
    [Arguments("flytrap")]
    [Arguments("archer")]
    [Arguments("bigMonster")]
    [Arguments("wildBoar")]
    public async Task CombatBehaviorEnter_WithoutOptionalNpcSkills_StartsCombatOnce(string kind)
    {
        var world = CreateWorld(true);
        var npc = CreateNpc(world);
        var starts = 0;
        world.Events.OnUnitCombatStart += (_, _) => starts++;
        Behavior behavior = kind switch
        {
            "common" => new AttackBehavior(),
            "almighty" => new AlmightyAttackBehavior(),
            "flytrap" => new FlytrapAttackBehavior(),
            "archer" => new ArcherAttackBehavior(),
            "bigMonster" => new BigMonsterAttackBehavior(),
            "wildBoar" => new WildBoarAttackBehavior(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        behavior.Ai = npc.Ai;

        behavior.Enter();
        behavior.Enter();

        await Assert.That(starts).IsEqualTo(1);
        await Assert.That(npc.IsInBattle).IsTrue();
    }

    [Test]
    public async Task FollowPathEnter_IdlePatrol_DoesNotStartCombat()
    {
        var world = CreateWorld(true);
        var npc = CreateNpc(world);
        var starts = 0;
        var localStarts = 0;
        world.Events.OnUnitCombatStart += (_, _) => starts++;
        npc.Events.OnCombatStarted += (_, _) =>
        {
            localStarts++;
            npc.IsInBattle = true;
        };
        var behavior = new FollowPathBehavior { Ai = npc.Ai };

        behavior.Enter();

        await Assert.That(starts).IsEqualTo(0);
        await Assert.That(localStarts).IsEqualTo(0);
        await Assert.That(npc.IsInBattle).IsFalse();
        await Assert.That(npc.IsInPatrol).IsTrue();
    }

    [Test]
    public async Task ReturnStateEnter_Reset_RaisesCombatEndOnce()
    {
        var world = CreateWorld(true);
        var npc = CreateNpc(world);
        npc.Buffs = Mock.Of<IBuffs>().Object;
        npc.IsInBattle = true;
        var ends = 0;
        world.Events.OnUnitCombatEnd += (_, _) => ends++;
        var behavior = new ReturnStateBehavior { Ai = npc.Ai };

        behavior.Enter();
        behavior.Enter();

        await Assert.That(ends).IsEqualTo(1);
        await Assert.That(npc.IsInBattle).IsFalse();
    }

    private WorldInstance CreateWorld(bool dungeon)
    {
        var template = new WorldTemplate { Id = 1, ZoneKeys = [1] };
        var world = new WorldInstance(template, 0, true, 1);
        _worlds[world.Id] = world;
        if (dungeon)
            _ = new Dungeon(new IndunZone(), world);
        return world;
    }

    private static RecordingUnit CreateUnit(WorldInstance world, uint objectId)
    {
        return new RecordingUnit { ObjId = objectId, Hp = 100, MaxHp = 100, ParentWorld = world };
    }

    private static RecordingNpc CreateNpc(WorldInstance world)
    {
        var npc = new RecordingNpc { ObjId = 1, Hp = 100, MaxHp = 100, Template = new NpcTemplate(), ParentWorld = world };
        npc.Ai = new DummyAiCharacter { Owner = npc };
        return npc;
    }

    private sealed class RecordingUnit : Unit
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }

    private sealed class RecordingNpc : Npc
    {
        public override int MaxHp { get; set; }
        public override int MaxMp { get; set; }
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }
}
