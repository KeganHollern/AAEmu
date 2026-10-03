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

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcAggroLifecycleTests
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
        ReplaceSingleton(new NpcGameData());
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
    [Arguments(true)]
    [Arguments(false)]
    public async Task Death_RemovesTargetSubscriptionsAndLateHealCannotRestoreAggro(bool characterTarget)
    {
        var npc = CreateNpc();
        var target = characterTarget ? (Unit)CreateCharacter() : CreateUnit();
        npc.AddUnitAggro(AggroKind.Heal, target, 100);
        await AssertSubscribed(npc, target);

        npc.Hp = 0;
        npc.DoDie(npc, KillReason.Damage);
        npc.DoDie(npc, KillReason.Damage);

        await AssertDetached(npc, target);
        target.Events.OnHealed(target, new OnHealedArgs { Healer = target, HealAmount = 200 });
        await Assert.That(npc.AggroTable).IsEmpty();
        if (target is Character character)
        {
            await Assert.That(character.IsInAggroListOf).IsEmpty();
            await Assert.That(character.IsInBattle).IsFalse();
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ScheduledRemoval_CleansAggroEvenWhenTargetAlreadyLeftWorld(bool targetInWorld)
    {
        var npc = CreateNpc();
        var target = CreateCharacter();
        npc.AddUnitAggro(AggroKind.Heal, target, 100);
        await AssertSubscribed(npc, target);
        var spawner = new NpcSpawner
        {
            ParentWorld = _world, SpawnerId = 123,
            Template = new NpcSpawnerTemplate { Id = 123 }
        };
        npc.Spawner = spawner;
        spawner.SpawnedNpcs[spawner.SpawnerId] = [npc];
        if (!targetInWorld)
            _world.RemoveObject(target);
        npc.Despawn = DateTime.UtcNow;
        _world.SpawnManager.AddDespawn(npc);

        _world.SpawnManager.DespawnObject(npc);
        _world.SpawnManager.DespawnObject(npc);

        await Assert.That(_world.GetUnit(npc.ObjId)).IsNull();
        await Assert.That(npc.Despawned).IsTrue();
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
        await AssertDetached(npc, target);
        target.Events.OnHealed(target, new OnHealedArgs { Healer = target, HealAmount = 200 });
        await Assert.That(npc.AggroTable).IsEmpty();
        await Assert.That(target.IsInAggroListOf).IsEmpty();
        await Assert.That(target.IsInBattle).IsFalse();
    }

    [Test]
    public async Task DirectDelete_LeavesQueuedSpawnerCleanupAndDoesNotReleaseAReusedIdTwice()
    {
        var npc = CreateNpc();
        var target = CreateCharacter();
        npc.AddUnitAggro(AggroKind.Heal, target, 100);
        var inFlightHealHandlers = target.Events.OnHealed;
        var spawner = new NpcSpawner
        {
            ParentWorld = _world, SpawnerId = 123,
            Template = new NpcSpawnerTemplate { Id = 123 }
        };
        npc.Spawner = spawner;
        spawner.SpawnedNpcs[spawner.SpawnerId] = [npc];
        npc.Despawn = DateTime.UtcNow;
        _world.SpawnManager.AddDespawn(npc);

        npc.Delete();

        await Assert.That(npc.CombatRetired).IsTrue();
        await Assert.That(npc.Despawned).IsFalse();
        await Assert.That(_world.GetUnit(npc.ObjId)).IsNull();
        await Assert.That(spawner.SpawnedNpcs[spawner.SpawnerId]).HasSingleItem();
        await AssertDetached(npc, target);
        await Assert.That(target.IsInAggroListOf).IsEmpty();
        await Assert.That(target.IsInBattle).IsFalse();
        inFlightHealHandlers(target, new OnHealedArgs { Healer = target, HealAmount = 200 });
        npc.AddUnitAggro(AggroKind.Damage, target, 25);
        await Assert.That(npc.AggroTable).IsEmpty();
        await Assert.That(npc.CharacterTagging.Tagger).IsNull();

        _world.SpawnManager.DespawnObject(npc);

        await Assert.That(npc.Despawned).IsTrue();
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
        var reusedId = ObjectIdManager.Instance.GetNextId();
        await Assert.That(reusedId).IsEqualTo(npc.ObjId);
        var replacement = new Unit { ObjId = reusedId, ParentWorld = _world, Hp = 100 };
        _world.AddObject(replacement);

        // Simulate a stale queued callback after the allocator gave this ID to another unit.
        npc.Despawn = DateTime.UtcNow;
        _world.SpawnManager.AddDespawn(npc);
        _world.SpawnManager.DespawnObject(npc);
        _world.SpawnManager.DespawnObject(npc);

        await Assert.That(_world.GetUnit(reusedId)).IsSameReferenceAs(replacement);
        await Assert.That(ObjectIdManager.Instance.GetNextId()).IsNotEqualTo(reusedId);
        await AssertDetached(npc, target);
        await Assert.That(target.IsInAggroListOf).IsEmpty();
    }

    [Test]
    public async Task DirectDelete_PreservesOtherNpcSubscriptionsAndCombatUntilItsRemoval()
    {
        var first = CreateNpc();
        var second = CreateNpc();
        var target = CreateCharacter();
        first.AddUnitAggro(AggroKind.Heal, target, 100);
        second.AddUnitAggro(AggroKind.Heal, target, 100);
        var previousThreat = second.AggroTable[target.ObjId].TotalAggro;

        first.Delete();

        await AssertDetached(first, target);
        await AssertSubscribed(second, target);
        await Assert.That(target.IsInAggroListOf.Count).IsEqualTo(1);
        await Assert.That(target.IsInBattle).IsTrue();
        target.Events.OnHealed(target, new OnHealedArgs { Healer = target, HealAmount = 100 });
        await Assert.That(first.AggroTable).IsEmpty();
        await Assert.That(second.AggroTable[target.ObjId].TotalAggro).IsEqualTo(previousThreat + 60);

        second.Delete();

        await AssertDetached(second, target);
        await Assert.That(target.IsInAggroListOf).IsEmpty();
        await Assert.That(target.IsInBattle).IsFalse();
    }

    [Test]
    public async Task ClearAllAggro_RemovesSubscriptionsWithoutRemovingUnrelatedHandlers()
    {
        var npc = CreateNpc();
        var target = CreateCharacter();
        var heals = 0;
        var deaths = 0;
        target.Events.OnHealed += (_, _) => heals++;
        target.Events.OnDeath += (_, _) => deaths++;
        npc.AddUnitAggro(AggroKind.Heal, target, 100);
        await AssertSubscribed(npc, target);
        _world.RemoveObject(target);

        npc.ClearAllAggro();
        npc.ClearAllAggro();
        target.Events.OnHealed(target, new OnHealedArgs { Healer = target, HealAmount = 200 });
        target.Events.OnDeath(target, new OnDeathArgs { Victim = target, Killer = target });

        await AssertDetached(npc, target);
        await Assert.That(heals).IsEqualTo(1);
        await Assert.That(deaths).IsEqualTo(1);
        await Assert.That(target.IsInAggroListOf).IsEmpty();
    }

    [Test]
    public async Task GroupRetirement_RemovesEveryMembersTargetSubscriptionsAndReverseLinks()
    {
        var first = CreateNpc();
        var second = CreateNpc();
        var target = CreateCharacter();
        var group = new NpcGroupInstance(new NpcGroup { Id = 50 },
            new NpcSpawner { ParentWorld = _world }, new WorldSpawnPosition());
        group.Attach(new NpcGroupMember { Id = 1, NpcGroupId = 50, NpcId = 100 }, first);
        group.Attach(new NpcGroupMember { Id = 2, NpcGroupId = 50, NpcId = 100 }, second);
        first.AddUnitAggro(AggroKind.Heal, target, 100);
        second.AddUnitAggro(AggroKind.Heal, target, 100);
        await AssertSubscribed(first, target);
        await AssertSubscribed(second, target);

        group.Retire();
        group.Retire();
        target.Events.OnHealed(target, new OnHealedArgs { Healer = target, HealAmount = 100 });

        await Assert.That(group.IsRetired).IsTrue();
        await Assert.That(group.GetMembers()).IsEmpty();
        await Assert.That(first.GroupInstance).IsNull();
        await Assert.That(second.GroupInstance).IsNull();
        await AssertDetached(first, target);
        await AssertDetached(second, target);
        await Assert.That(target.IsInAggroListOf).IsEmpty();
        await Assert.That(target.IsInBattle).IsFalse();
    }

    private QuietNpc CreateNpc()
    {
        var npc = new QuietNpc
        {
            ObjId = ObjectIdManager.Instance.GetNextId(), TemplateId = 100,
            Template = new NpcTemplate(), ParentWorld = _world, Hp = 100
        };
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

    private static async Task AssertSubscribed(Npc npc, Unit target)
    {
        await Assert.That(target.Events.OnHealed.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, npc))).IsEqualTo(1);
        await Assert.That(target.Events.OnDeath.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, npc))).IsEqualTo(1);
        await Assert.That(npc.AggroTable.ContainsKey(target.ObjId)).IsTrue();
        if (target is Character character)
            await Assert.That(character.IsInAggroListOf.ContainsKey(npc.ObjId)).IsTrue();
    }

    private static async Task AssertDetached(Npc npc, Unit target)
    {
        await Assert.That(target.Events.OnHealed.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, npc))).IsFalse();
        await Assert.That(target.Events.OnDeath.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, npc))).IsFalse();
        await Assert.That(npc.AggroTable).IsEmpty();
    }

    private void ReplaceSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private sealed class QuietNpc : Npc
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }
}
