using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.AI.v2.Behaviors;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Flytrap;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcGroupCombatTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private WorldInstance _world;
    private uint _nextId;

    [Before(Test)]
    public void SetUp()
    {
        _nextId = 100;
        var worlds = new WorldManager(null, null, null, null, null);
        ReplaceSingleton(worlds);
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new QuestManager(null, null));
        ReplaceSingleton(new NpcGameData());
        ReplaceSingleton(new UnitAttributeLimitsGameData());
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        ReplaceSingleton(skills);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
        instances[_world.Id] = _world;
    }

    [After(Test)]
    public void TearDown()
    {
        if (_world != null)
            GC.SuppressFinalize(_world);
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    public async Task DamageOnThreeMembers_SharesTheSumOnceAndKeepsQuestEventsLocal()
    {
        var group = CreateGroup();
        var members = new[] { Member(group, 10), Member(group, 20), Member(group, 30) };
        var target = Character();
        var questEvents = 0;
        target.Events.OnAggro += (_, _) => questEvents++;

        members[0].OnDamageReceived(target, 10);
        members[1].OnDamageReceived(target, 25);
        members[2].OnDamageReceived(target, 5);

        await AssertThreat(members, target, 40, 0);
        await Assert.That(questEvents).IsEqualTo(3);
        foreach (var npc in members)
            await AssertSubscriptions(npc, target, 1);
        await Assert.That(target.IsInAggroListOf.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DamageModifiers_ApplyOnceAtEachDamagedMember()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        Bonus(target, UnitAttribute.AggroMul, 100);
        Bonus(first, UnitAttribute.IncomingAggroMul, 50);
        Bonus(second, UnitAttribute.IncomingAggroMul, 200);

        first.OnDamageReceived(target, 10);
        await AssertThreat([first, second], target, 30, 0);
        second.OnDamageReceived(target, 10);

        await AssertThreat([first, second], target, 90, 0);
    }

    [Test]
    public async Task CopiedDamage_DoesNotGrantDamageTagsOrContributorCredit()
    {
        var group = CreateGroup();
        var source = Member(group, 10);
        var helper = Member(group, 20);
        var target = Character();

        source.OnDamageReceived(target, 70);

        await Assert.That(source.CharacterTagging.Tagger).IsSameReferenceAs(target);
        await Assert.That(helper.CharacterTagging.Tagger).IsNull();
        await Assert.That(helper.CharacterTagging.TagTeam).IsEqualTo(0u);
        await Assert.That(helper.CharacterTagging.GetAllContributors(100)).IsEmpty();
        await AssertThreat([source, helper], target, 70, 0);
    }

    [Test]
    public async Task OneHealEvent_ReachesThreeSubscribersButAddsThreatOnce()
    {
        var group = CreateGroup();
        var members = new[] { Member(group, 10), Member(group, 20), Member(group, 30) };
        var target = Character();
        var healer = Character();
        members[0].OnDamageReceived(target, 10);
        var args = new OnHealedArgs { Healer = healer, HealAmount = 100 };

        target.Events.OnHealed(target, args);
        target.Events.OnHealed(target, args);

        await AssertThreat(members, healer, 0, 60);
        foreach (var npc in members)
            await AssertSubscriptions(npc, healer, 1);

        target.Events.OnHealed(target, new OnHealedArgs { Healer = healer, HealAmount = 100 });
        await AssertThreat(members, healer, 0, 120);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task HealModifiers_UseStableMemberRowOrderRatherThanSubscriptionOrder(bool lowerRowSubscribesFirst)
    {
        var group = CreateGroup();
        // Object IDs and attach order deliberately disagree with the authored member row order.
        var higherRow = Member(group, 20);
        var lowerRow = Member(group, 10);
        var target = Character();
        var healer = Character();
        Bonus(lowerRow, UnitAttribute.IncomingAggroMul, 100);
        Bonus(higherRow, UnitAttribute.IncomingAggroMul, 400);
        Bonus(healer, UnitAttribute.AggroMul, 50);
        (lowerRowSubscribesFirst ? lowerRow : higherRow).OnDamageReceived(target, 1);

        target.Events.OnHealed(target, new OnHealedArgs { Healer = healer, HealAmount = 100 });

        await AssertThreat([lowerRow, higherRow], healer, 0, 180);
    }

    [Test]
    public async Task DirectHealingThreat_AppliesTheHealFactorOnceBeforeSharedCopies()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var healer = Character();
        Bonus(first, UnitAttribute.IncomingAggroMul, 100);
        Bonus(second, UnitAttribute.IncomingAggroMul, 400);
        Bonus(healer, UnitAttribute.AggroMul, 50);

        first.AddUnitAggro(AggroKind.Heal, healer, 100);

        await AssertThreat([first, second], healer, 0, 180);
        await Assert.That(first.CharacterTagging.Tagger).IsNull();
        await Assert.That(second.CharacterTagging.Tagger).IsNull();
    }

    [Test]
    [Arguments("dead")]
    [Arguments("despawned")]
    [Arguments("unregistered")]
    [Arguments("retired-ai")]
    [Arguments("returning-before-buff")]
    public async Task IneligibleFirstHealSubscriber_DoesNotConsumeTheEvent(string state)
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        var healer = Character();
        first.OnDamageReceived(target, 10);
        MakeIneligible(first, state);

        target.Events.OnHealed(target, new OnHealedArgs { Healer = healer, HealAmount = 100 });

        await Assert.That(first.AggroTable.ContainsKey(healer.ObjId)).IsFalse();
        await AssertThreat([second], healer, 0, 60);
    }

    [Test]
    [Arguments("dead")]
    [Arguments("despawned")]
    [Arguments("unregistered")]
    [Arguments("retired-ai")]
    [Arguments("returning-before-buff")]
    public async Task IneligibleMember_DoesNotReceiveSharedDamage(string state)
    {
        var group = CreateGroup();
        var source = Member(group, 10);
        var helper = Member(group, 20);
        var target = Character();
        MakeIneligible(helper, state);

        source.OnDamageReceived(target, 10);

        await AssertThreat([source], target, 10, 0);
        await Assert.That(helper.AggroTable).IsEmpty();
        await AssertSubscriptions(helper, target, 0);
    }

    [Test]
    public async Task ClearedExpectedEntry_CannotRestoreTheLastPrunedTarget()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        first.OnDamageReceived(target, 10);
        var stale = first.AggroTable[target.ObjId];
        first.ClearAllAggro();
        second.ClearAllAggro();

        group.AddSharedThreat(first, target, stale, 50, 0);
        var replacement = Member(group, 30);
        group.SynchronizeCombat(replacement);

        await Assert.That(first.AggroTable).IsEmpty();
        await Assert.That(second.AggroTable).IsEmpty();
        await Assert.That(replacement.AggroTable).IsEmpty();
        await Assert.That(SharedTargetCount(group)).IsEqualTo(0);
    }

    [Test]
    public async Task ReplacedExpectedEntry_CannotAddItsOldDeltaToTheNewEntry()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        first.OnDamageReceived(target, 10);
        var stale = first.AggroTable[target.ObjId];
        first.ClearAllAggro();
        second.ClearAllAggro();
        first.OnDamageReceived(target, 3);

        group.AddSharedThreat(first, target, stale, 50, 0);

        await AssertThreat([first, second], target, 3, 0);
    }

    [Test]
    public async Task Detach_PrunesOnlyAfterTheLastSurvivingHolderLeaves()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        first.OnDamageReceived(target, 10);

        group.Detach(first);
        await Assert.That(SharedTargetCount(group)).IsEqualTo(1);
        group.Detach(second);

        await Assert.That(SharedTargetCount(group)).IsEqualTo(0);
        var replacement = Member(group, 30);
        group.SynchronizeCombat(replacement);
        await Assert.That(replacement.AggroTable).IsEmpty();
    }

    [Test]
    public async Task ReplacementMember_GetsCurrentValuesAndOneSubscriptionWithoutDamageTags()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var oldMember = Member(group, 20);
        var target = Character();
        var healer = Character();
        first.OnDamageReceived(target, 15);
        target.Events.OnHealed(target, new OnHealedArgs { Healer = healer, HealAmount = 100 });
        group.Detach(oldMember);
        oldMember.ClearAllAggro();
        var replacement = Member(group, 20);

        group.SynchronizeCombat(replacement);
        group.SynchronizeCombat(replacement);

        await AssertThreat([first, replacement], target, 15, 0);
        await AssertThreat([first, replacement], healer, 0, 60);
        await AssertSubscriptions(replacement, target, 1);
        await AssertSubscriptions(replacement, healer, 1);
        await AssertSubscriptions(oldMember, target, 0);
        await Assert.That(replacement.CharacterTagging.Tagger).IsNull();
    }

    [Test]
    public async Task PreparedMember_ReceivesNoThreatUntilWorldPublication()
    {
        var group = CreateGroup();
        var source = Member(group, 10);
        var prepared = Member(group, 20, published: false);
        var target = Character();
        source.OnDamageReceived(target, 10);

        group.SynchronizeCombat(prepared);
        await Assert.That(prepared.AggroTable).IsEmpty();
        await AssertSubscriptions(prepared, target, 0);
        _world.AddObject(prepared);
        group.SynchronizeCombat(prepared);

        await AssertThreat([source, prepared], target, 10, 0);
        await AssertSubscriptions(prepared, target, 1);
    }

    [Test]
    public async Task HealDeduplication_IsPerOccurrenceEvenWithTheSameTemplate()
    {
        var firstGroup = CreateGroup();
        var secondGroup = CreateGroup();
        var firstMembers = new[] { Member(firstGroup, 10), Member(firstGroup, 20) };
        var secondMembers = new[] { Member(secondGroup, 10), Member(secondGroup, 20) };
        var target = Character();
        var healer = Character();
        firstMembers[0].OnDamageReceived(target, 10);
        secondMembers[0].OnDamageReceived(target, 20);

        target.Events.OnHealed(target, new OnHealedArgs { Healer = healer, HealAmount = 100 });

        await AssertThreat(firstMembers, target, 10, 0);
        await AssertThreat(secondMembers, target, 20, 0);
        await AssertThreat(firstMembers.Concat(secondMembers), healer, 0, 60);
    }

    [Test]
    [Arguments(NpcGroupAggroRuleKind.None)]
    [Arguments(NpcGroupAggroRuleKind.AggroLink)]
    public async Task OtherRules_DoNotCopyDamageOrHealValues(NpcGroupAggroRuleKind rule)
    {
        var group = CreateGroup(rule);
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        var healer = Character();
        first.OnDamageReceived(target, 10);
        target.Events.OnHealed(target, new OnHealedArgs { Healer = healer, HealAmount = 100 });

        await AssertThreat([first], target, 10, 0);
        await AssertThreat([first], healer, 0, 60);
        await Assert.That(second.AggroTable).IsEmpty();
        await Assert.That(SharedTargetCount(group)).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentDamage_KeepsEveryDeltaWithoutMultiplicationOrDeadlock()
    {
        var group = CreateGroup();
        var members = new[] { Member(group, 10), Member(group, 20), Member(group, 30) };
        var target = Character();

        await Task.WhenAll(Enumerable.Range(0, 60)
            .Select(index => Task.Run(() => members[index % members.Length].AddUnitAggro(AggroKind.Damage, target, 2))))
            .WaitAsync(TimeSpan.FromSeconds(10));

        await AssertThreat(members, target, 120, 0);
        foreach (var npc in members)
            await AssertSubscriptions(npc, target, 1);
    }

    [Test]
    public async Task ConcurrentDamageAndExternalDeathHandlers_PreserveEverySubscription()
    {
        var group = CreateGroup();
        var members = new[] { Member(group, 10), Member(group, 20), Member(group, 30) };
        var target = Character();
        var called = new int[40];
        var handlers = Enumerable.Range(0, called.Length).Select(index =>
            new EventHandler<OnDeathArgs>((_, _) => Interlocked.Increment(ref called[index]))).ToArray();
        var mutations = handlers.Select((handler, index) => Task.Run(() =>
        {
            target.Events.AddDeathHandler(handler);
            Thread.Yield();
            if (index % 2 == 0)
                target.Events.RemoveDeathHandler(handler);
        })).Concat(members.Select(member => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                member.AddUnitAggro(AggroKind.Damage, target, 2);
                Thread.Yield();
            }
        })));

        await Task.WhenAll(mutations).WaitAsync(TimeSpan.FromSeconds(10));

        await AssertThreat(members, target, 120, 0);
        foreach (var npc in members)
            await AssertSubscriptions(npc, target, 1);
        target.Hp = 0;
        target.Events.OnDeath(target, new OnDeathArgs { Victim = target, Killer = members[0] });
        for (var i = 0; i < called.Length; i++)
            await Assert.That(called[i]).IsEqualTo(i % 2);
        foreach (var npc in members)
        {
            await Assert.That(npc.AggroTable).IsEmpty();
            await AssertSubscriptions(npc, target, 0);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SharedTargetSelectors_ReplacePriorTargetsAndResolveEqualThreatByObjectId(bool flytrap, bool geodata)
    {
        var previousServices = SingletonContainer.ServiceProvider;
        using var services = new ServiceCollection()
            .AddSingleton<IOptions<AppConfiguration>>(Options.Create(new AppConfiguration
            {
                World = new WorldConfig { GeoDataMode = geodata }
            }))
            .BuildServiceProvider();
        SingletonContainer.ServiceProvider = services;
        try
        {
            var group = CreateGroup();
            var first = Member(group, 10);
            var second = Member(group, 20);
            var lowerId = Character();
            var higherId = Character();
            first.OnDamageReceived(higherId, 30);
            second.OnDamageReceived(lowerId, 10);
            first.SetTarget(higherId);
            second.SetTarget(lowerId);

            foreach (var npc in new[] { first, second })
            {
                await Assert.That(SelectTarget(npc, flytrap)).IsTrue();
                await Assert.That(npc.CurrentTarget).IsSameReferenceAs(higherId);
            }

            second.OnDamageReceived(lowerId, 30);
            foreach (var npc in new[] { first, second })
            {
                await Assert.That(SelectTarget(npc, flytrap)).IsTrue();
                await Assert.That(npc.CurrentTarget).IsSameReferenceAs(lowerId);
            }

            first.OnDamageReceived(higherId, 10);
            foreach (var npc in new[] { first, second })
            {
                // Force the wrong prior target again so tie handling must make a choice.
                npc.SetTarget(higherId);
                await Assert.That(SelectTarget(npc, flytrap)).IsTrue();
                await Assert.That(npc.CurrentTarget).IsSameReferenceAs(lowerId);
            }
        }
        finally
        {
            SingletonContainer.ServiceProvider = previousServices;
        }
    }

    [Test]
    public async Task TargetDeath_CleansEveryMemberAndPrunesTheSharedMap()
    {
        var group = CreateGroup();
        var members = new[] { Member(group, 10), Member(group, 20) };
        var target = Character();
        members[0].OnDamageReceived(target, 10);

        target.Hp = 0;
        target.Events.OnDeath(target, new OnDeathArgs { Victim = target, Killer = members[0] });

        foreach (var npc in members)
        {
            await Assert.That(npc.AggroTable).IsEmpty();
            await AssertSubscriptions(npc, target, 0);
        }
        await Assert.That(SharedTargetCount(group)).IsEqualTo(0);
    }

    [Test]
    public async Task ReusedTargetId_ReplacesOwnerSubscriptionsAndStartsNewThreat()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var oldTarget = Character();
        first.OnDamageReceived(oldTarget, 10);
        _world.RemoveObject(oldTarget);
        var newTarget = Character(oldTarget.ObjId);

        first.OnDamageReceived(newTarget, 3);

        await AssertThreat([first, second], newTarget, 3, 0);
        foreach (var npc in new[] { first, second })
        {
            await Assert.That(npc.AggroTable[newTarget.ObjId].Owner).IsSameReferenceAs(newTarget);
            await AssertSubscriptions(npc, oldTarget, 0);
            await AssertSubscriptions(npc, newTarget, 1);
        }
    }

    [Test]
    public async Task LateOldTargetDeath_CannotRemoveThreatForTheReusedId()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var oldTarget = Character();
        first.OnDamageReceived(oldTarget, 10);
        var inFlightDeathHandlers = oldTarget.Events.OnDeath;
        _world.RemoveObject(oldTarget);
        var newTarget = Character(oldTarget.ObjId);
        first.OnDamageReceived(newTarget, 3);

        inFlightDeathHandlers(oldTarget, new OnDeathArgs { Victim = oldTarget, Killer = first });

        await AssertThreat([first, second], newTarget, 3, 0);
        await Assert.That(newTarget.IsInAggroListOf.Count).IsEqualTo(2);
        foreach (var npc in new[] { first, second })
            await AssertSubscriptions(npc, newTarget, 1);
    }

    [Test]
    public async Task QuestCallback_RunsAfterGroupAndLocalLocksAreReleased()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        var combatLock = typeof(NpcGroupInstance).GetField("_combatSync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(group)!;
        var callbackCount = 0;
        var heldLock = false;
        target.Events.OnAggro += (_, _) =>
        {
            callbackCount++;
            heldLock |= Monitor.IsEntered(combatLock) || Monitor.IsEntered(first.AggroTable) || Monitor.IsEntered(second.AggroTable);
            // This reentrant cleanup must not leave the copied target behind.
            first.ClearAllAggro();
            second.ClearAllAggro();
        };

        first.OnDamageReceived(target, 10);

        await Assert.That(callbackCount).IsEqualTo(1);
        await Assert.That(heldLock).IsFalse();
        await Assert.That(first.AggroTable).IsEmpty();
        await Assert.That(second.AggroTable).IsEmpty();
        await Assert.That(SharedTargetCount(group)).IsEqualTo(0);
    }

    [Test]
    public async Task Retire_DiscardsCentralReferencesAndRejectsSavedPropagation()
    {
        var group = CreateGroup();
        var first = Member(group, 10);
        var second = Member(group, 20);
        var target = Character();
        first.OnDamageReceived(target, 10);
        var expected = first.AggroTable[target.ObjId];
        var inFlightHealHandlers = target.Events.OnHealed;

        group.Retire();
        group.AddSharedThreat(first, target, expected, 20, 0);
        group.SynchronizeCombat(second);
        inFlightHealHandlers(target, new OnHealedArgs { Healer = target, HealAmount = 100 });

        await Assert.That(SharedTargetCount(group)).IsEqualTo(0);
        await Assert.That(group.GetMembers()).IsEmpty();
        await Assert.That(first.GroupInstance).IsNull();
        await Assert.That(second.GroupInstance).IsNull();
        await Assert.That(first.AggroTable).IsEmpty();
        await Assert.That(second.AggroTable).IsEmpty();
        await AssertSubscriptions(first, target, 0);
        await AssertSubscriptions(second, target, 0);
        await Assert.That(target.IsInAggroListOf).IsEmpty();
    }

    private NpcGroupInstance CreateGroup(NpcGroupAggroRuleKind rule = NpcGroupAggroRuleKind.AggroShare) => new(
        new NpcGroup { Id = 50, AggroRuleId = (int)rule },
        new NpcSpawner { ParentWorld = _world }, new WorldSpawnPosition());

    private QuietNpc Member(NpcGroupInstance group, int row, bool published = true)
    {
        var npc = new QuietNpc
        {
            ObjId = _nextId++, TemplateId = (uint)(1000 + row), ParentWorld = _world, Hp = 100,
            Template = new NpcTemplate()
        };
        npc.Ai = new ProbeAi { Owner = npc };
        group.Attach(new NpcGroupMember { Id = row, NpcGroupId = group.Template.Id, NpcId = (int)npc.TemplateId }, npc);
        if (published)
            _world.AddObject(npc);
        return npc;
    }

    private Character Character(uint? objectId = null)
    {
        var id = objectId ?? _nextId++;
        var character = new Character(null) { Id = id, ObjId = id, ParentWorld = _world, Hp = 100 };
        _world.AddObject(character);
        return character;
    }

    private void MakeIneligible(Npc npc, string state)
    {
        switch (state)
        {
            case "dead": npc.Hp = 0; break;
            case "despawned": npc.Despawned = true; break;
            case "unregistered": _world.RemoveObject(npc); break;
            case "retired-ai": npc.Ai.Owner = null; break;
            case "returning-before-buff":
                typeof(NpcAi).GetField("_currentBehavior", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(npc.Ai, new ReturnStateBehavior { Ai = npc.Ai });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static void Bonus(Unit unit, UnitAttribute attribute, int value) => unit.AddBonus((uint)attribute, new Bonus
    {
        Template = new BonusTemplate { Attribute = attribute, ModifierType = UnitModifierType.Value, Value = value },
        Value = value
    });

    private static async Task AssertThreat(IEnumerable<Npc> members, Unit target, int damage, int heal)
    {
        foreach (var member in members)
        {
            await Assert.That(member.AggroTable.ContainsKey(target.ObjId)).IsTrue();
            var aggro = member.AggroTable[target.ObjId];
            await Assert.That(aggro.DamageAggro).IsEqualTo(damage);
            await Assert.That(aggro.HealAggro).IsEqualTo(heal);
        }
    }

    private static async Task AssertSubscriptions(Npc npc, Unit target, int count)
    {
        await Assert.That(target.Events.OnHealed.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, npc)))
            .IsEqualTo(count);
        await Assert.That(target.Events.OnDeath.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, npc)))
            .IsEqualTo(count);
    }

    private static int SharedTargetCount(NpcGroupInstance group) =>
        ((IDictionary)typeof(NpcGroupInstance).GetField("_sharedThreat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(group)!).Count;

    private static bool SelectTarget(Npc npc, bool flytrap)
    {
        if (!flytrap)
            return new ProbeCombatBehavior { Ai = npc.Ai }.UpdateTarget();
        var behavior = new FlytrapAttackBehavior { Ai = npc.Ai };
        return (bool)typeof(FlytrapAttackBehavior).GetMethod("UpdateTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(behavior, null)!;
    }

    private void ReplaceSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private static void SetField<T>(T value, string field, object setting) =>
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, setting);

    private sealed class QuietNpc : Npc
    {
        public override int MaxHp { get; set; } = 100;
        public override bool UnitIsVisible(BaseUnit unit) => unit != null;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class ProbeAi : NpcAi
    {
        protected override void Build() { }
    }

    private sealed class ProbeCombatBehavior : BaseCombatBehavior
    {
        public override void Enter() { }
        public override void Tick(TimeSpan delta) { }
        public override void Exit() { }
    }
}
