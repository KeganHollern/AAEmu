using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Spheres;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestFollowUnitTests
{
    private readonly Dictionary<FieldInfo, object> _previousSingletons = [];
    private readonly List<Quest> _quests = [];
    private readonly List<Quest> _saved = [];
    private QuestInteractionTestModels _models;
    private QuestManager _manager;
    private WorldInstance _world;
    private CharacterMock _owner;
    private bool _previousDebug;

    [Before(Test)]
    public void SetUp()
    {
        _previousDebug = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        _models = new QuestInteractionTestModels();
        _manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        Install(_manager);
        Install(new UnitRequirementsGameData());
        Install(new TaskManager(Mock.Of<ITickManager>().Object));
        Install(new ExpressTextManager());
        var spheres = new SphereGameData();
        SetField(spheres, "_spheres", new Dictionary<uint, Spheres>());
        SetField(spheres, "_sphereQuests", new Dictionary<uint, SphereQuests>());
        Install(spheres);
        var worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(worlds);
        _world = new WorldInstance(new WorldTemplate { Id = 1, Name = "quest_follow_test" }, 0, true, 1);
        _world.SphereQuestManager = new SphereQuestManager(_world);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            new Dictionary<uint, WorldInstance> { [1] = _world }));
        var ids = new QuestIdManager();
        typeof(IdManager).GetField("_freeIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ids, new BitSet(1024));
        var idsField = typeof(QuestIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSingletons.Add(idsField, idsField.GetValue(null));
        idsField.SetValue(null, ids);
        _owner = CreateOwner(7, 70);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var quest in _quests)
            quest.FinalizeQuestActs();
        _models.Dispose();
        AppConfiguration.Instance.DebugInfo = _previousDebug;
        foreach (var (field, previous) in _previousSingletons)
            field.SetValue(null, previous);
        _previousSingletons.Clear();
        _quests.Clear();
        _saved.Clear();
    }

    [Test]
    public async Task AcceptThenTalk_UsesExactTalkedNpc_AndSkillIgnoresEarlierMatchingClone()
    {
        var template = CreateTemplate();
        Register(template);
        var giver = CreateNpc(81, 5053);
        var clone = CreateNpc(82, 5055);
        var follower = CreateNpc(83, 5055);

        var accepted = _owner.Quests.AddQuestFromNpc(template.Id, giver.ObjId);
        var quest = _owner.Quests.ActiveQuests[template.Id];
        _quests.Add(quest);
        _manager.DoQueuedEvaluations();
        await Assert.That(accepted).IsTrue();
        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();

        Talk(quest, follower);
        _manager.DoQueuedEvaluations();

        await Assert.That(follower.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        await Assert.That(clone.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(follower.Skills).IsEquivalentTo(new[] { 13613u });
        await Assert.That(clone.Skills).IsEmpty();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task Talk_PendingArrival_StartsBeforeSphereObjectiveCompletes()
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);

        Talk(quest, follower);
        _manager.DoQueuedEvaluations();

        await Assert.That(follower.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(quest.Objectives[1]).IsEqualTo(0);
    }

    [Test]
    [Arguments("quest")]
    [Arguments("component")]
    [Arguments("act")]
    [Arguments("npc")]
    [Arguments("source_player")]
    public async Task Talk_WrongAuthoredSource_DoesNotStartOrCreditFollow(string mismatch)
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, mismatch == "npc" ? 999u : 4729u);
        var talk = TalkAct(quest);
        var source = mismatch == "source_player" ? CreateOwner(8, 80) : _owner;

        _manager.DoTalkMadeEvents(source, _owner, follower.ObjId,
            mismatch == "quest" ? 999u : quest.TemplateId,
            mismatch == "component" ? 999u : talk.QuestComponent.Template.Id,
            mismatch == "act" ? 999u : talk.Id);
        _manager.DoQueuedEvaluations();

        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
    }

    [Test]
    public async Task Talk_RepeatedOrMatchingClone_DoesNotReplaceExactClaimOrRepeatSkill()
    {
        var quest = CreateActiveQuest();
        var follower = CreateNpc(81, 5055);
        var clone = CreateNpc(82, 5055);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        _owner.CurrentTarget = clone;
        Talk(quest, follower);
        Talk(quest, clone);
        quest.TryApplyNpcControl(quest.Template.Components[3274]);

        await Assert.That(follower.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        await Assert.That(clone.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(follower.Skills.Count).IsEqualTo(1);
        await Assert.That(quest.TryGetFollowNpc(5055, out var bound)).IsTrue();
        await Assert.That(bound).IsSameReferenceAs(follower);
    }

    [Test]
    public async Task Talk_TwoOwners_CannotStealOrReleaseFirstClaim()
    {
        var first = CreateActiveQuest(reindeer: true);
        var secondOwner = CreateOwner(8, 80);
        var second = CreateActiveQuest(reindeer: true, owner: secondOwner);
        var follower = CreateNpc(81, 4729);
        Talk(first, follower);
        Talk(second, follower);
        _manager.DoQueuedEvaluations();
        second.FinalizeQuestActs();

        await Assert.That(follower.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        await Assert.That(follower.Ai.IsQuestFollowOwner(first)).IsTrue();
        await Assert.That(follower.Ai.IsQuestFollowOwner(second)).IsFalse();
        first.FinalizeQuestActs();
        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();
    }

    [Test]
    public async Task Talk_TwoQuestsForSameOwner_CannotShareOneNpcClaim()
    {
        var first = CreateActiveQuest(reindeer: true);
        var second = CreateActiveQuest(reindeer: true, questId: 1040);
        var follower = CreateNpc(81, 4729);
        Talk(first, follower);
        Talk(second, follower);
        _manager.DoQueuedEvaluations();
        second.ReleaseNpcControl();

        await Assert.That(follower.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        await Assert.That(follower.Ai.IsQuestFollowOwner(first)).IsTrue();
        await Assert.That(follower.Ai.IsQuestFollowOwner(second)).IsFalse();
    }

    [Test]
    [Arguments(QuestComponentKind.Fail)]
    [Arguments(QuestComponentKind.Drop)]
    [Arguments(QuestComponentKind.Reward)]
    public async Task TerminalStep_ReleasesFollowWithoutChangingAnotherNpc(QuestComponentKind step)
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);
        var other = CreateNpc(82, 4729);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        quest.Step = step;

        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(other.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(follower.Ai.IsQuestFollowOwner(quest)).IsFalse();
    }

    [Test]
    public async Task Ready_KeepsFollowerUntilCompletionCleanup()
    {
        var quest = CreateActiveQuest();
        var follower = CreateNpc(81, 5055);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(follower.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        quest.Complete();
        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();
    }

    [Test]
    public async Task Disconnect_ReleasesFollowerAndAllOwnHooks()
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        _owner.Quests.PrepareEscortQuestsForDisconnect();
        _owner.Events.OnDisconnect(_owner, new OnDisconnectArgs { Player = _owner });

        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(follower.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
        await Assert.That(_owner.Events.OnDisconnect.GetInvocationList().Length).IsEqualTo(1);
    }

    [Test]
    public async Task RemovedNpc_CallbackDoesNotWaitForPersistenceLock()
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        bool finished;
        lock (SaveManager.PersistenceSyncRoot)
        {
            var removal = Task.Run(follower.Delete);
            finished = removal.Wait(TimeSpan.FromSeconds(2));
        }
        _manager.DoQueuedEvaluations();
        follower.Ai?.Tick(TimeSpan.Zero);

        await Assert.That(finished).IsTrue();
        await Assert.That(quest.TryGetFollowNpc(4729, out _)).IsFalse();
    }

    [Test]
    [Arguments("npc_death")]
    [Arguments("npc_despawn")]
    [Arguments("owner_death")]
    [Arguments("owner_world")]
    [Arguments("owner_instance")]
    public async Task LostSource_ReleasesExactClaimWithoutBindingMatchingClone(string loss)
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);
        var clone = CreateNpc(82, 4729);
        var ai = follower.Ai;
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        switch (loss)
        {
            case "npc_death":
                follower.Hp = 0;
                follower.Events.OnDeath(follower, new OnDeathArgs { Victim = follower, Killer = follower });
                break;
            case "npc_despawn":
                follower.Despawned = true;
                break;
            case "owner_death":
                _owner.Hp = 0;
                break;
            case "owner_world":
                SetField(_owner, "_parentWorld", new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 2), typeof(GameObject));
                break;
            case "owner_instance":
                SetField(_owner.Transform, "_instanceId", 9U);
                break;
        }
        ai.Tick(TimeSpan.Zero);
        _manager.DoQueuedEvaluations();

        await Assert.That(ai.IsQuestFollowOwner(quest)).IsFalse();
        await Assert.That(clone.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(quest.TryGetFollowNpc(4729, out _)).IsFalse();
    }

    [Test]
    [Arguments(QuestComponentKind.Progress)]
    [Arguments(QuestComponentKind.Ready)]
    public async Task Reload_DoesNotBindCurrentTargetOrMatchingWorldNpc(QuestComponentKind savedStep)
    {
        var quest = CreateActiveQuest(reindeer: savedStep == QuestComponentKind.Progress);
        var follower = CreateNpc(81, savedStep == QuestComponentKind.Progress ? 4729u : 5055u);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        var data = quest.WriteData();
        var status = quest.Status;
        quest.FinalizeQuestActs();
        _owner.Quests.ActiveQuests.Remove(quest.TemplateId);
        var clone = CreateNpc(82, follower.TemplateId);
        _owner.CurrentTarget = clone;
        var restored = NewQuest((QuestTemplate)quest.Template, _owner);
        restored.Status = status;
        restored.ReadData(data);
        _owner.Quests.AddLoadedQuest(restored);
        _manager.DoQueuedEvaluations();

        await Assert.That(restored.Step).IsEqualTo(savedStep);
        await Assert.That(clone.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(follower.Ai.AiFollowUnitObj).IsNull();
        await Assert.That(restored.TryGetFollowNpc(follower.TemplateId, out _)).IsFalse();
        if (savedStep == QuestComponentKind.Progress)
        {
            Talk(restored, clone);
            _manager.DoQueuedEvaluations();
            await Assert.That(clone.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        }
    }

    [Test]
    public async Task Sphere_RequiresOwnerAndExactFollowerInsideAuthoredDestination()
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);
        var clone = CreateNpc(82, 4729);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        var sphere = new SphereQuest { QuestId = quest.TemplateId, ComponentId = 5094, Xyz = new Vector3(100, 0, 0), Radius = 10 };
        var trigger = new SphereQuestTrigger { Owner = _owner, Quest = quest, Sphere = sphere, NpcTemplate = 4729, SphereId = 943 };
        _world.SphereQuestManager.AddSphereQuestTrigger(trigger);
        var entered = 0;
        var exited = 0;
        _owner.Events.OnEnterSphere += (_, _) => entered++;
        _owner.Events.OnExitSphere += (_, _) => exited++;
        _owner.Transform.Local.Position = Vector3.Zero;
        follower.Transform.Local.Position = Vector3.Zero;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(entered).IsEqualTo(0);

        _owner.Transform.Local.Position = sphere.Xyz;
        follower.Transform.Local.Position = new Vector3(0, 0, 0);
        clone.Transform.Local.Position = sphere.Xyz;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(entered).IsEqualTo(0);

        follower.Transform.Local.Position = sphere.Xyz + new Vector3(0, 0, 10);
        trigger.Tick(TimeSpan.Zero);
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(entered).IsEqualTo(1);
        await Assert.That(quest.Objectives[1]).IsEqualTo(1);
        follower.Transform.Local.Position = sphere.Xyz + new Vector3(0, 0, 10.001f);
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(exited).IsEqualTo(1);
        await Assert.That(quest.Objectives[1]).IsEqualTo(0);
    }

    [Test]
    [Arguments("npc_death")]
    [Arguments("npc_despawn")]
    [Arguments("owner_world")]
    [Arguments("outside_destination")]
    public async Task Sphere_StalePositiveObjective_DoesNotCompleteAfterSourceLoss(string loss)
    {
        var quest = CreateActiveQuest(reindeer: true);
        var follower = CreateNpc(81, 4729);
        Talk(quest, follower);
        _manager.DoQueuedEvaluations();
        var sphere = new SphereQuest { QuestId = quest.TemplateId, ComponentId = 5094, Radius = 10 };
        var trigger = new SphereQuestTrigger { Owner = _owner, Quest = quest, Sphere = sphere, NpcTemplate = 4729, SphereId = 943 };
        _world.SphereQuestManager.AddSphereQuestTrigger(trigger);
        quest.Objectives[1] = 1;
        switch (loss)
        {
            case "npc_death": follower.Hp = 0; break;
            case "npc_despawn": follower.Despawned = true; break;
            case "owner_world":
                SetField(_owner, "_parentWorld", new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 2), typeof(GameObject));
                break;
            case "outside_destination": follower.Transform.Local.Position = new Vector3(0, 0, 10.001f); break;
        }

        var completed = quest.RunCurrentStep();

        await Assert.That(completed).IsFalse();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
    }

    [Test]
    public async Task Talk_AfterReleasedLease_RebindsExactNpcWithoutRepeatedSkill()
    {
        var quest = CreateActiveQuest(reindeer: true);
        var component = quest.Template.Components[5093];
        component.SkillId = 13613;
        var first = CreateNpc(81, 4729);
        var second = CreateNpc(82, 4729);
        Talk(quest, first);
        _manager.DoQueuedEvaluations();
        first.Ai.AiFollowUnitObj = CreateNpc(83, 999);
        first.Ai.Tick(TimeSpan.Zero);
        _manager.DoQueuedEvaluations();

        Talk(quest, second);
        _manager.DoQueuedEvaluations();

        await Assert.That(second.Ai.AiFollowUnitObj).IsSameReferenceAs(_owner);
        await Assert.That(first.Skills.Count).IsEqualTo(1);
        await Assert.That(second.Skills).IsEmpty();
        await Assert.That(quest.TryGetFollowNpc(4729, out var bound)).IsTrue();
        await Assert.That(bound).IsSameReferenceAs(second);
    }

    private CharacterMock CreateOwner(uint id, uint objId)
    {
        var owner = new CharacterMock { Id = id, ObjId = objId, Name = "Questor", ModelId = 1, Hp = 100 };
        SetField(owner, "<IsOnline>k__BackingField", true, typeof(Character));
        owner.Quests = new CharacterQuests(owner, _ => true, _ => { }, _saved.Add, new GameScheduleManager(null, null));
        SetField(owner, "_parentWorld", _world, typeof(GameObject));
        owner.Region = _owner?.Region ?? QuestInteractionTestModels.CreateRegion(_world);
        return owner;
    }

    private ProbeNpc CreateNpc(uint objId, uint templateId)
    {
        var npc = new ProbeNpc
        {
            ObjId = objId, TemplateId = templateId, ModelId = 1, Hp = 100, IsVisible = true, DisabledSetPosition = true,
            Template = new NpcTemplate { Id = templateId, Scale = 1, ModelId = 1 }
        };
        SetField(npc, "_parentWorld", _world, typeof(GameObject));
        npc.Region = _owner.Region;
        _world.AddObject(npc);
        npc.Ai = new ProbeAi { Owner = npc, _nextAlertCheckTime = DateTime.MaxValue };
        npc.Ai.Start();
        npc.Ai.GoToIdle();
        return npc;
    }

    private Quest CreateActiveQuest(bool reindeer = false, CharacterMock owner = null, uint questId = 0)
    {
        owner ??= _owner;
        var quest = NewQuest(CreateTemplate(reindeer, questId), owner);
        owner.Quests.ActiveQuests.Add(quest.TemplateId, quest);
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        quest.QuestInitialized();
        return quest;
    }

    private Quest NewQuest(QuestTemplate template, CharacterMock owner)
    {
        var quest = new Quest(template, owner, _manager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object);
        _quests.Add(quest);
        return quest;
    }

    private static QuestTemplate CreateTemplate(bool reindeer = false, uint questId = 0)
    {
        var template = new QuestTemplate { Id = questId == 0 ? reindeer ? 1039u : 851u : questId };
        var start = new QuestComponentTemplate(template) { Id = reindeer ? 5091u : 3273u, KindId = QuestComponentKind.Start };
        start.ActTemplates.Add(new QuestActConAcceptNpc(start) { ActId = 4729, NpcId = 5053 });
        template.Components.Add(start.Id, start);
        var progress = new QuestComponentTemplate(template)
        {
            Id = reindeer ? 5093u : 3274u, KindId = QuestComponentKind.Progress,
            NpcAiId = QuestNpcAiName.FollowUnit, NpcId = reindeer ? 4729u : 5055u,
            SkillId = reindeer ? 0u : 13613u
        };
        progress.ActTemplates.Add(new QuestActObjTalk(progress)
        {
            ActId = reindeer ? 12752u : 4730u, NpcId = progress.NpcId, Count = 1, ThisComponentObjectiveIndex = 0
        });
        template.Components.Add(progress.Id, progress);
        if (reindeer)
        {
            var arrival = new QuestComponentTemplate(template) { Id = 5094, KindId = QuestComponentKind.Progress };
            arrival.ActTemplates.Add(new QuestActObjSphere(arrival) { ActId = 12754, SphereId = 943, NpcId = 4729, Count = 1, ThisComponentObjectiveIndex = 1 });
            arrival.ActTemplates.Add(new QuestActCheckCompleteComponent(arrival) { ActId = 13129, CompleteComponent = progress.Id });
            template.Components.Add(arrival.Id, arrival);
        }
        var ready = new QuestComponentTemplate(template) { Id = 3275, KindId = QuestComponentKind.Ready };
        ready.ActTemplates.Add(new QuestActConReportNpc(ready) { ActId = 9000, NpcId = 5054 });
        template.Components.Add(ready.Id, ready);
        template.Components.Add(3276, new QuestComponentTemplate(template) { Id = 3276, KindId = QuestComponentKind.Reward });
        return template;
    }

    private void Register(QuestTemplate template) => ((Dictionary<uint, QuestTemplate>)typeof(QuestManager)
        .GetField("_questTemplates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!).Add(template.Id, template);

    private static QuestAct TalkAct(Quest quest) => quest.QuestSteps[QuestComponentKind.Progress].Components.Values
        .SelectMany(component => component.Acts).Single(act => act.Template is QuestActObjTalk);

    private void Talk(Quest quest, Npc npc)
    {
        var talk = TalkAct(quest);
        _manager.DoTalkMadeEvents(quest.Owner, quest.Owner, npc.ObjId, quest.TemplateId, talk.QuestComponent.Template.Id, talk.Id);
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSingletons.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value, Type declaringType = null) =>
        (declaringType ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ProbeNpc : MovementProbeNpc
    {
        public List<uint> Skills { get; } = [];
        public override SkillResult UseSkill(uint skillId, IUnit target)
        {
            Skills.Add(skillId);
            return SkillResult.Success;
        }
        public override void Hide() => IsVisible = false;
    }

    private sealed class ProbeAi : NpcAi
    {
        protected override void Build()
        {
            AddBehavior(BehaviorKind.FollowUnit, new FollowUnitBehavior());
            AddBehavior(BehaviorKind.Idle, new DoNothingBehavior());
            AddBehavior(BehaviorKind.DoNothing, new DoNothingBehavior()).SetDefaultBehavior();
        }
    }
}
