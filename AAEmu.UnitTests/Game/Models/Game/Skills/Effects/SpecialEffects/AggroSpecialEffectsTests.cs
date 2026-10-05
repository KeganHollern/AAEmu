using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

[NotInParallel]
public sealed partial class AggroSpecialEffectsTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private IServiceProvider _previousServices;
    private ServiceProvider _services;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        _previousServices = SingletonContainer.ServiceProvider;
        _services = new ServiceCollection()
            .AddSingleton<IOptions<AppConfiguration>>(Options.Create(new AppConfiguration
            {
                World = new WorldConfig { TagShareEnabled = false }
            }))
            .BuildServiceProvider();
        SingletonContainer.ServiceProvider = _services;
        var worlds = new WorldManager(null, null, null, null, null);
        ReplaceSingleton(worlds);
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_taggedBuffs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, List<uint>>());
        ReplaceSingleton(skills);
        ReplaceSingleton(new QuestManager(null, null));
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new GameScheduleManager(null, TimeProvider.System));
        ReplaceSingleton(new NpcGameData());
        ReplaceSingleton(new MateGameData());
        ReplaceSingleton(new UnitAttributeLimitsGameData());
        var ids = new ObjectIdManager();
        ids.Initialize();
        var idField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((idField, idField.GetValue(null)));
        idField.SetValue(null, ids);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        _world.SpawnManager = new SpawnManager(_world);
        var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
        instances[_world.Id] = _world;
    }

    [After(Test)]
    public void TearDown()
    {
        if (_world != null)
            GC.SuppressFinalize(_world);
        foreach (var (field, previous) in _singletons)
            field.SetValue(null, previous);
        _singletons.Clear();
        SingletonContainer.ServiceProvider = _previousServices;
        _services.Dispose();
    }

    [Test]
    public async Task AggroCopy_PreservesResolvedAmountsWithoutDamageCreditAndKeepsSubscriptions()
    {
        var source = CreateNpc();
        var recipient = CreateNpc();
        var player = CreateCharacter();
        source.AddUnitAggro(AggroKind.Damage, player, 25);
        source.AddUnitAggro(AggroKind.Heal, player, 100);
        recipient.AddBonus(900, new Bonus
        {
            Template = new BonusTemplate { Attribute = UnitAttribute.IncomingAggroMul, ModifierType = UnitModifierType.Value, Value = 100 },
            Value = 100
        });
        var aggroEvents = 0;
        player.Events.OnAggro += (_, _) => aggroEvents++;

        Execute(new AggroCopy(), source, recipient);
        Execute(new AggroCopy(), source, recipient);

        await Assert.That(recipient.AggroTable[player.ObjId].DamageAggro).IsEqualTo(25);
        await Assert.That(recipient.AggroTable[player.ObjId].HealAggro).IsEqualTo(60);
        await Assert.That(recipient.CharacterTagging.Tagger).IsNull();
        await Assert.That(aggroEvents).IsEqualTo(0);
        await Assert.That(player.IsInAggroListOf.ContainsKey(recipient.ObjId)).IsTrue();
        await Assert.That(player.Events.OnHealed.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, recipient))).IsEqualTo(1);

        player.Events.OnHealed(player, new OnHealedArgs { Healer = player, HealAmount = 100 });

        await Assert.That(recipient.AggroTable[player.ObjId].HealAggro).IsEqualTo(180);
        await Assert.That(source.AggroTable[player.ObjId].HealAggro).IsEqualTo(120);
    }

    [Test]
    public async Task AggroCopy_SharedGroupRetainsImportedThreatOnLaterDamage()
    {
        var source = CreateNpc();
        var first = CreateNpc();
        var second = CreateNpc();
        var player = CreateCharacter();
        var group = new NpcGroupInstance(new NpcGroup { Id = 50, AggroRuleId = (int)NpcGroupAggroRuleKind.AggroShare },
            new NpcSpawner { ParentWorld = _world }, new WorldSpawnPosition());
        group.Attach(new NpcGroupMember { Id = 1, NpcGroupId = 50 }, first);
        group.Attach(new NpcGroupMember { Id = 2, NpcGroupId = 50 }, second);
        source.AddUnitAggro(AggroKind.Damage, player, 50);
        source.AddUnitAggro(AggroKind.Heal, player, 100);

        Execute(new AggroCopy(), source, first);
        second.AddUnitAggro(AggroKind.Damage, player, 10);

        foreach (var npc in new[] { first, second })
        {
            await Assert.That(npc.AggroTable[player.ObjId].DamageAggro).IsEqualTo(60);
            await Assert.That(npc.AggroTable[player.ObjId].HealAggro).IsEqualTo(60);
        }
        await Assert.That(first.CharacterTagging.Tagger).IsNull();
    }

    [Test]
    public async Task AggroCopy_DoesNotCopyDeadOrUnregisteredTargets()
    {
        var source = CreateNpc();
        var recipient = CreateNpc();
        var dead = CreateCharacter();
        var departed = CreateCharacter();
        source.AddUnitAggro(AggroKind.Heal, dead, 10);
        source.AddUnitAggro(AggroKind.Heal, departed, 10);
        dead.Hp = 0;
        _world.RemoveObject(departed);

        Execute(new AggroCopy(), source, recipient);

        await Assert.That(recipient.AggroTable).IsEmpty();
    }

    [Test]
    public async Task RemoveIncomingThreat_CleansReverseLinksAndOnlyMatchingSelections()
    {
        var forgotten = CreateCharacter();
        var retained = CreateCharacter();
        var first = CreateNpc();
        var second = CreateNpc();
        first.AddUnitAggro(AggroKind.Heal, forgotten, 100);
        first.AddUnitAggro(AggroKind.Heal, retained, 50);
        second.AddUnitAggro(AggroKind.Heal, forgotten, 100);
        first.CurrentTarget = retained;
        second.CurrentTarget = forgotten;
        second.CurrentAggroTarget = forgotten;
        var beforeHeal = forgotten.Events.OnHealed;

        forgotten.RemoveIncomingThreat();
        await Assert.That(forgotten.IsInAggroListOf).IsEmpty();
        await Assert.That(first.AggroTable.ContainsKey(forgotten.ObjId)).IsFalse();
        beforeHeal(forgotten, new OnHealedArgs { Healer = forgotten, HealAmount = 100 });

        await Assert.That(first.AggroTable.ContainsKey(forgotten.ObjId)).IsTrue();
        // First still has another combatant, so a real later heal can create new threat.
        // The empty second NPC must not re-enter combat from the saved callback.
        await Assert.That(second.AggroTable).IsEmpty();
        await Assert.That(first.CurrentTarget).IsSameReferenceAs(retained);
        await Assert.That(second.CurrentTarget).IsNull();
        await Assert.That(second.CurrentAggroTarget).IsNull();
    }

    [Test]
    public async Task ClearAggroOfNpc_ChecksOwnerCombatStateWithoutChangingTheOtherNpc()
    {
        var owner = CreateNpc();
        var target = CreateNpc();
        owner.AddUnitAggro(AggroKind.Heal, target, 50);
        owner.IsInBattle = true;
        target.IsInBattle = true;

        owner.ClearAggroOfUnit(target);

        await Assert.That(owner.IsInBattle).IsFalse();
        await Assert.That(target.IsInBattle).IsTrue();
    }

    [Test]
    public async Task LoseTarget_ClearsSelectionAndKeepsThreatWithExactPacketBody()
    {
        var affected = CreateNpc();
        var other = CreateNpc();
        affected.AddUnitAggro(AggroKind.Heal, other, 50);
        affected.CurrentTarget = other;

        Execute(new LoseTarget(), other, affected);

        await Assert.That(affected.CurrentTarget).IsNull();
        await Assert.That(affected.AggroTable.ContainsKey(other.ObjId)).IsTrue();
        var packet = affected.Sent.OfType<SCTargetChangedPacket>().Single();
        var body = packet.Write(new PacketStream());
        await Assert.That(body.ReadBc()).IsEqualTo(affected.ObjId);
        await Assert.That(body.ReadBc()).IsEqualTo(0u);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task NpcDespawn_RemovesImmediatelyWithoutDeathAndOnlyOneIdRelease(bool withSpawner)
    {
        var npc = CreateNpc();
        var player = CreateCharacter();
        npc.AddUnitAggro(AggroKind.Heal, player, 100);
        var deaths = 0;
        npc.Events.OnDeath += (_, _) => deaths++;
        if (withSpawner)
        {
            npc.Spawner = new NpcSpawner
            {
                ParentWorld = _world, SpawnerId = 123,
                Template = new NpcSpawnerTemplate { Id = 123, MaxPopulation = 1 }, RespawnTime = 60
            };
            npc.Spawner.SpawnedNpcs[123] = [npc];
        }

        Execute(new NpcDespawn(), player, npc);
        Execute(new NpcDespawn(), player, npc);

        await Assert.That(_world.GetUnit(npc.ObjId)).IsNull();
        await Assert.That(npc.Despawned).IsTrue();
        await Assert.That(npc.Hp).IsEqualTo(100);
        await Assert.That(npc.DeadTime).IsEqualTo(DateTime.MinValue);
        await Assert.That(deaths).IsEqualTo(0);
        await Assert.That(npc.AggroTable).IsEmpty();
        await Assert.That(player.IsInAggroListOf).IsEmpty();
        if (withSpawner)
        {
            await Assert.That(npc.Spawner.SpawnedNpcs).IsEmpty();
            await Assert.That(npc.Respawn).IsGreaterThan(DateTime.UtcNow);
        }
        var reused = ObjectIdManager.Instance.GetNextId();
        await Assert.That(reused).IsEqualTo(npc.ObjId);
        await Assert.That(ObjectIdManager.Instance.GetNextId()).IsNotEqualTo(reused);
    }

    [Test]
    public async Task NpcDespawn_AlreadyQueuedDeathKeepsOneOriginalReplacementTime()
    {
        var npc = CreateNpc();
        npc.Spawner = new NpcSpawner
        {
            ParentWorld = _world, SpawnerId = 123,
            Template = new NpcSpawnerTemplate { Id = 123, MaxPopulation = 1 }, RespawnTime = 60
        };
        npc.Spawner.SpawnedNpcs[123] = [npc];
        npc.Hp = 0;
        npc.DeadTime = DateTime.UtcNow.AddSeconds(-10);
        npc.Spawner.DoDespawn(npc);
        var replacementTime = npc.Respawn;

        Execute(new NpcDespawn(), npc, npc);

        await Assert.That(npc.Despawned).IsTrue();
        await Assert.That(npc.Respawn).IsEqualTo(replacementTime);
        var pending = (int)typeof(NpcSpawner).GetField("_scheduledCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(npc.Spawner)!;
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [Arguments("retired")]
    [Arguments("despawned")]
    [Arguments("alive")]
    public async Task DispelTask_OnlyLiveNpcRunsItsTimeoutEffects(string state)
    {
        var npc = CreateNpc();
        var buff = new Buff(npc, npc, null, new BuffTemplate { Id = 100 }, null, DateTime.UtcNow)
        {
            State = EffectState.Acting, InUse = true
        };
        var timeouts = 0;
        buff.Events.OnTimeout += (_, _) => timeouts++;
        if (state == "retired")
            npc.Delete();
        else if (state == "despawned")
            npc.Despawned = true;

        new DispelTask(buff).Execute();

        await Assert.That(timeouts).IsEqualTo(state == "alive" ? 1 : 0);
        await Assert.That(buff.State).IsEqualTo(state == "alive" ? EffectState.Finished : EffectState.Acting);
    }

    private static void Execute(SpecialEffectAction action, BaseUnit caster, BaseUnit target) =>
        action.Execute(caster, null, target, null, null, null, null, DateTime.UtcNow, 0, 0, 0, 0);

    private QuietNpc CreateNpc()
    {
        var npc = new QuietNpc
        {
            ObjId = ObjectIdManager.Instance.GetNextId(), TemplateId = 100,
            Template = new NpcTemplate(), ParentWorld = _world, Hp = 100
        };
        npc.Ai = new ProbeAi { Owner = npc };
        _world.AddObject(npc);
        return npc;
    }

    private Character CreateCharacter()
    {
        var id = ObjectIdManager.Instance.GetNextId();
        var character = new Character(null) { Id = id, ObjId = id, ParentWorld = _world, Hp = 100 };
        _world.AddObject(character);
        return character;
    }

    private Unit CreateUnit()
    {
        var unit = new Unit { ObjId = ObjectIdManager.Instance.GetNextId(), ParentWorld = _world, Hp = 100 };
        _world.AddObject(unit);
        return unit;
    }

    private void ReplaceSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private sealed class QuietNpc : Npc
    {
        public List<GamePacket> Sent { get; } = [];
        public override int MaxHp { get; set; } = 100;
        public override void BroadcastPacket(GamePacket packet, bool self) => Sent.Add(packet);
    }
    private sealed class ProbeAi : NpcAi
    {
        protected override void Build() { }
    }
}
