using System.Reflection;

using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Utils;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestGuardTests
{
    private const uint GuardTemplateId = 9846;
    private const uint QuestId = 3656;
    private readonly List<SavedQuestState> _saved = [];
    private readonly QuestManager _manager = new(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
    private readonly WorldInstance _world = new(new WorldTemplate { Id = 1 }, 0, true, 1);

    [Test]
    public async Task Talk_BindsExactOccurrence_AnotherMatchingNpcDoesNotFailQuest()
    {
        var (quest, owner, talk) = CreateQuest();
        var protectedNpc = CreateNpc(81);
        var other = CreateNpc(82);

        Talk(talk, owner, protectedNpc);
        Talk(talk, owner, other);
        Die(other);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);

        Die(protectedNpc);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Failed);
        await Assert.That(protectedNpc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
    }

    [Test]
    public async Task GuardBeforeTalk_DoesNotSelectNearbyOccurrence()
    {
        var (quest, owner, _) = CreateQuest();
        var nearby = CreateNpc(81);
        owner.CurrentTarget = nearby;

        Die(nearby);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.CheckGuard(GuardTemplateId)).IsTrue();
        quest.FinalizeQuestActs();
    }

    [Test]
    public async Task StartGuard_KeepsExactAcceptorThroughProgress()
    {
        var npc = CreateNpc(81);
        var (quest, owner, _) = CreateQuest(startGuard: true, acceptor: npc);
        var other = CreateNpc(82);
        owner.CurrentTarget = other;
        quest.Step = QuestComponentKind.Progress;

        Die(other);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        Die(npc);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
    }

    [Test]
    public async Task RemovalAfterFinalGuardCheck_PreventsReadyTransition()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        quest.Objectives[1] = 1;
        var template = Mock.Of<IQuestTemplate>();
        template.Components.Returns(quest.Template.Components);
        var scoreReads = 0;
        template.Score.Returns(() =>
        {
            // RunComponents reads Score first. The second read is the completion
            // override in RunCurrentStep, after its last guard check but before Ready.
            if (++scoreReads == 2)
                npc.Delete();
            return 0;
        });
        quest.Template = template.Object;

        var completed = quest.RunCurrentStep();

        await Assert.That(scoreReads).IsEqualTo(2);
        await Assert.That(completed).IsFalse();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Failed);
        _manager.DoQueuedEvaluations();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
    }

    [Test]
    public async Task RemovalAfterCompletionDecision_DoesNotFailReadyQuest()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        quest.Objectives[1] = 1;
        var progress = quest.QuestSteps[QuestComponentKind.Progress].Components[15273];
        progress.Acts.Add(new QuestAct(progress, new RemovalOnFinalize(progress.Template, npc)));

        var completed = quest.RunCurrentStep();
        _manager.DoQueuedEvaluations();

        await Assert.That(completed).IsTrue();
        await Assert.That(npc.CombatRetired).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Ready);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DirectReadyTransition_CannotOverrideGuardFailure(bool processFailureFirst)
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        npc.Delete();
        if (processFailureFirst)
            _manager.DoQueuedEvaluations();

        quest.Step = QuestComponentKind.Ready;

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Failed);
    }

    [Test]
    public async Task TalkCheckpoint_ReloadFailsWithoutSelectingAnotherNpc()
    {
        var (quest, owner, talk) = CreateQuest();
        Talk(talk, owner, CreateNpc(81));
        var saved = _saved.Single();
        var other = CreateNpc(82);

        var restored = Reload(quest, saved, other);

        await Assert.That(restored.Objectives[0]).IsEqualTo(1);
        await Assert.That(restored.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(restored.Status).IsEqualTo(QuestStatus.Failed);
        await Assert.That(_saved.Last().Status).IsEqualTo(QuestStatus.Failed);
        await Assert.That(other.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
    }

    [Test]
    [Arguments(QuestComponentKind.Start)]
    [Arguments(QuestComponentKind.Progress)]
    public async Task StartAcceptor_ReloadFailsWithoutTalkProgress(QuestComponentKind savedStep)
    {
        var (quest, owner, _) = CreateQuest(startGuard: true, acceptor: CreateNpc(81));
        quest.Step = savedStep;
        owner.Quests.PersistActiveQuest(quest);

        var restored = Reload(quest, _saved.Last());

        await Assert.That(restored.Objectives[0]).IsEqualTo(0);
        await Assert.That(restored.Step).IsEqualTo(QuestComponentKind.Fail);
    }

    [Test]
    public async Task UnstartedEscort_ReloadAndDisconnectKeepProgress()
    {
        var (quest, _, _) = CreateQuest();
        var saved = new SavedQuestState(quest.WriteData(), quest.Status);
        var restored = Reload(quest, saved, CreateNpc(81));

        restored.Owner.Quests.PrepareEscortQuestsForDisconnect();

        await Assert.That(restored.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(restored.Objectives[0]).IsEqualTo(0);
        await Assert.That(_saved).IsEmpty();
    }

    [Test]
    [Arguments(QuestComponentKind.Ready)]
    [Arguments(QuestComponentKind.Reward)]
    [Arguments(QuestComponentKind.Fail)]
    [Arguments(QuestComponentKind.Drop)]
    public async Task TerminalGuard_ReloadDoesNotChangeStep(QuestComponentKind terminal)
    {
        var (quest, owner, talk) = CreateQuest();
        Talk(talk, owner, CreateNpc(81));
        quest.Objectives[1] = 1;
        quest.Step = terminal;
        var saved = new SavedQuestState(quest.WriteData(), quest.Status);

        var restored = Reload(quest, saved);
        restored.Owner.Quests.PrepareEscortQuestsForDisconnect();

        await Assert.That(restored.Step).IsEqualTo(terminal);
        await Assert.That(restored.Objectives[0]).IsEqualTo(1);
        await Assert.That(restored.Objectives[1]).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyProgressWithCompleteCounters_ReloadFailsWithoutInventingLiveConditions()
    {
        var (quest, owner, talk) = CreateQuest();
        Talk(talk, owner, CreateNpc(81));
        quest.Objectives[1] = 1; // A pre-fix checkpoint can contain counters without an approved Ready transition.
        var saved = new SavedQuestState(quest.WriteData(), QuestStatus.Progress);

        var restored = Reload(quest, saved);

        await Assert.That(restored.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(restored.Objectives[1]).IsEqualTo(1);
    }

    [Test]
    public async Task Disconnect_FailsUnfinishedEscortBeforeSave_AndReleasesHooks()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);

        owner.Quests.PrepareEscortQuestsForDisconnect();
        owner.Events.OnDisconnect(owner, new OnDisconnectArgs { Player = owner });
        var saved = _saved.Last();

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(saved.Status).IsEqualTo(QuestStatus.Failed);
        await Assert.That(npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
        await Assert.That(owner.Events.OnDisconnect.GetInvocationList().Length).IsEqualTo(1);
        await Assert.That(Reload(quest, saved).Step).IsEqualTo(QuestComponentKind.Fail);
    }

    [Test]
    public async Task Disconnect_EvaluatesPendingCompleteProgressBeforeFailure()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        quest.Objectives[1] = 1;

        owner.Quests.PrepareEscortQuestsForDisconnect();
        var saved = _saved.Last();
        npc.Delete();

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(saved.Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(Reload(quest, saved).Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SphereCompletion_CheckpointsReadyBeforeEventReturns(bool useCheckSphere)
    {
        var (quest, owner, talk) = CreateQuest();
        _world.SphereQuestManager = new SphereQuestManager(_world);
        var component = quest.QuestSteps[QuestComponentKind.Progress].Components[15273];
        QuestActTemplate sphereTemplate = useCheckSphere
            ? new QuestActCheckSphere(component.Template) { ActId = 21290 }
            : new QuestActObjSphere(component.Template) { ActId = 21290, Count = 1, ThisComponentObjectiveIndex = 1 };
        component.Template.ActTemplates[1] = sphereTemplate;
        var sphereAct = new QuestAct(component, sphereTemplate);
        component.Acts[1] = sphereAct;
        Talk(talk, owner, CreateNpc(81));
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);

        sphereAct.OnEnterSphere(owner, new OnEnterSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = QuestId, ComponentId = component.Template.Id }
        });

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(_saved.Last().Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(Reload(quest, _saved.Last()).Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task CapturedTalkAfterUnstartedDisconnect_DoesNotStartOrPersistEscort()
    {
        var (quest, owner, talk) = CreateQuest();
        owner.Quests.PrepareEscortQuestsForDisconnect();

        Talk(talk, owner, CreateNpc(81));

        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(_saved).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedSphereExitAfterCompletion_KeepsCompletedProgress(bool useCheckSphere)
    {
        var (quest, owner, talk) = CreateQuest();
        _world.SphereQuestManager = new SphereQuestManager(_world);
        var component = quest.QuestSteps[QuestComponentKind.Progress].Components[15273];
        QuestActTemplate sphereTemplate = useCheckSphere
            ? new QuestActCheckSphere(component.Template) { ActId = 21290 }
            : new QuestActObjSphere(component.Template) { ActId = 21290, Count = 1, ThisComponentObjectiveIndex = 1 };
        component.Template.ActTemplates[1] = sphereTemplate;
        var sphereAct = new QuestAct(component, sphereTemplate);
        component.Acts[1] = sphereAct;
        Talk(talk, owner, CreateNpc(81));
        var sphere = new SphereQuest { QuestId = QuestId, ComponentId = component.Template.Id };
        sphereAct.OnEnterSphere(owner, new OnEnterSphereArgs { SphereQuest = sphere });
        var writes = _saved.Count;

        sphereAct.OnExitSphere(owner, new OnExitSphereArgs { SphereQuest = sphere });

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(_saved.Count).IsEqualTo(writes);
        if (useCheckSphere)
            await Assert.That(sphereAct.OverrideObjectiveCompleted).IsTrue();
        else
            await Assert.That(quest.Objectives[1]).IsEqualTo(1);
    }

    [Test]
    public async Task AddQuestFromNpc_BindsValidatedAcceptorBeforeActiveQuestRegistration()
    {
        using var models = new QuestInteractionTestModels();
        var dependencies = new Dictionary<FieldInfo, object>();
        void Install<T>(T instance) where T : class
        {
            var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            dependencies.Add(field, field.GetValue(null));
            field.SetValue(null, instance);
        }
        var oldDebug = AppConfiguration.Instance.DebugInfo;
        Quest accepted = null;
        try
        {
            AppConfiguration.Instance.DebugInfo = false;
            Install(_manager);
            Install(new UnitRequirementsGameData());
            Install(new TaskManager(Mock.Of<ITickManager>().Object));
            Install(new ExpressTextManager());
            Install(new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));
            var ids = new QuestIdManager();
            typeof(IdManager).GetField("_freeIds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ids, new BitSet(32));
            var idsField = typeof(QuestIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            dependencies.Add(idsField, idsField.GetValue(null));
            idsField.SetValue(null, ids);

            var npc = CreateNpc(81);
            var (fixture, owner, _) = CreateQuest(startGuard: true);
            var template = (QuestTemplate)fixture.Template;
            var start = template.Components[15272];
            start.ActTemplates.Add(new QuestActConAcceptNpc(start) { ActId = 20949, NpcId = GuardTemplateId });
            fixture.FinalizeQuestActs();
            var saved = new List<Quest>();
            owner.Quests = new CharacterQuests(owner, _ => true, _ => { }, saved.Add, new GameScheduleManager(null, null));
            owner.ModelId = npc.ModelId = 1;
            owner.Region = npc.Region = QuestInteractionTestModels.CreateRegion(_world);
            npc.IsVisible = true;
            _world.AddObject(npc);
            var templates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
                .GetField("_questTemplates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!;
            templates.Add(QuestId, template);

            var result = owner.Quests.AddQuestFromNpc(QuestId, npc.ObjId);
            accepted = owner.Quests.ActiveQuests[QuestId];
            _manager.DoQueuedEvaluations();

            await Assert.That(result).IsTrue();
            await Assert.That(owner.CurrentTarget).IsSameReferenceAs(npc);
            await Assert.That(accepted.Step).IsEqualTo(QuestComponentKind.Progress);
            await Assert.That(saved).Contains(accepted);
            Die(npc);
            await Assert.That(accepted.Step).IsEqualTo(QuestComponentKind.Fail);
        }
        finally
        {
            accepted?.FinalizeQuestActs();
            AppConfiguration.Instance.DebugInfo = oldDebug;
            foreach (var (field, previous) in dependencies)
                field.SetValue(null, previous);
        }
    }

    [Test]
    public async Task RemovalCallback_DoesNotWaitForQuestPersistenceLock()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        bool finished;
        lock (SaveManager.PersistenceSyncRoot)
        {
            var removal = Task.Run(npc.Delete);
            finished = removal.Wait(TimeSpan.FromSeconds(2));
        }
        _manager.DoQueuedEvaluations();

        await Assert.That(finished).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
    }

    [Test]
    public async Task Delete_AliveGuardWithoutAi_FailsQuestAndRemovesWorldObject()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        _world.AddObject(npc);
        Talk(talk, owner, npc);

        npc.Delete();
        _manager.DoQueuedEvaluations();

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(_world.GetNpc(npc.ObjId)).IsNull();
        await Assert.That(npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
    }

    [Test]
    [Arguments(QuestComponentKind.Ready)]
    [Arguments(QuestComponentKind.Reward)]
    [Arguments(QuestComponentKind.Drop)]
    [Arguments(QuestComponentKind.Fail)]
    public async Task TerminalStep_ReleasesGuard_DeathDoesNotChangeResult(QuestComponentKind terminal)
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);

        quest.Step = terminal;
        Die(npc);

        await Assert.That(quest.Step).IsEqualTo(terminal);
        await Assert.That(npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
        await Assert.That(owner.Events.OnQuestStepChanged.GetInvocationList().Length).IsEqualTo(1);
    }

    [Test]
    public async Task FinalizeQuestActs_ReleasesGuard_WithoutChangingAnotherAttempt()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        quest.FinalizeQuestActs();

        Die(npc);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(npc.Events.OnDeath.GetInvocationList().Length).IsEqualTo(1);
        await Assert.That(owner.Events.OnDisconnect.GetInvocationList().Length).IsEqualTo(1);
    }

    [Test]
    public async Task BindGuard_OtherWorld_IsRejected()
    {
        var (quest, _, _) = CreateQuest();
        var npc = CreateNpc(81);
        SetParentWorld(npc, new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 2));

        quest.BindGuardNpc(npc);
        Die(npc);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        quest.FinalizeQuestActs();
    }

    [Test]
    public async Task BoundDeath_FailsOnlyOwningQuestAttempt()
    {
        var (quest, owner, talk) = CreateQuest();
        var npc = CreateNpc(81);
        Talk(talk, owner, npc);
        var (otherQuest, otherOwner, otherTalk) = CreateQuest();
        var otherNpc = CreateNpc(82);
        Talk(otherTalk, otherOwner, otherNpc);

        Die(npc);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(otherQuest.Step).IsEqualTo(QuestComponentKind.Progress);
        otherQuest.FinalizeQuestActs();
    }

    private (Quest Quest, CharacterMock Owner, QuestAct Talk) CreateQuest(bool startGuard = false, Npc acceptor = null)
    {
        var owner = new CharacterMock { Id = 7, ObjId = 70, Name = "Questor", CurrentTarget = acceptor };
        owner.Quests = new CharacterQuests(owner, _ => true, _ => { }, q => _saved.Add(new(q.WriteData(), q.Status)));
        SetParentWorld(owner, _world);
        var template = new QuestTemplate { Id = QuestId };
        var start = new QuestComponentTemplate(template) { Id = 15272, KindId = QuestComponentKind.Start };
        var progress = new QuestComponentTemplate(template) { Id = 15273, KindId = QuestComponentKind.Progress };
        var talkTemplate = new QuestActObjTalk(progress)
        {
            ActId = 20950,
            NpcId = GuardTemplateId,
            Count = 1,
            ThisComponentObjectiveIndex = 0
        };
        progress.ActTemplates.Add(talkTemplate);
        progress.ActTemplates.Add(new PendingObjective(progress) { ActId = 21290, Count = 1, ThisComponentObjectiveIndex = 1 });
        var guardComponent = startGuard ? start : progress;
        guardComponent.ActTemplates.Add(new QuestActCheckGuard(guardComponent) { ActId = 20951, NpcId = GuardTemplateId });
        template.Components.Add(start.Id, start);
        template.Components.Add(progress.Id, progress);
        template.Components.Add(15276, new QuestComponentTemplate(template) { Id = 15276, KindId = QuestComponentKind.Ready });
        var quest = new Quest(template, owner, _manager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            QuestAcceptorType = acceptor == null ? QuestAcceptorType.Unknown : QuestAcceptorType.Npc,
            AcceptorId = acceptor?.TemplateId ?? 0
        };
        owner.Quests.ActiveQuests.Add(quest.TemplateId, quest);
        quest.Step = startGuard ? QuestComponentKind.Start : QuestComponentKind.Progress;
        return (quest, owner, quest.QuestSteps[QuestComponentKind.Progress].Components[progress.Id].Acts[0]);
    }

    private Quest Reload(Quest source, SavedQuestState saved, Npc currentTarget = null)
    {
        source.FinalizeQuestActs();
        var owner = new CharacterMock { Id = 7, ObjId = 71, Name = "Questor", CurrentTarget = currentTarget };
        SetParentWorld(owner, _world);
        owner.Quests = new CharacterQuests(owner, _ => true, _ => { }, q => _saved.Add(new(q.WriteData(), q.Status)));
        var restored = new Quest(source.Template, owner, _manager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Id = source.Id,
            Status = saved.Status
        };
        restored.ReadData(saved.Data);
        owner.Quests.AddLoadedQuest(restored);
        return restored;
    }

    private sealed record SavedQuestState(byte[] Data, QuestStatus Status);

    private TestNpc CreateNpc(uint objectId)
    {
        var npc = new TestNpc { ObjId = objectId, TemplateId = GuardTemplateId, Template = new NpcTemplate { Scale = 1 }, Hp = 100 };
        SetParentWorld(npc, _world);
        return npc;
    }

    private static void Talk(QuestAct talk, CharacterMock owner, Npc npc) => talk.OnTalkMade(owner, new OnTalkMadeArgs
    {
        QuestId = QuestId,
        NpcId = npc.TemplateId,
        SourcePlayer = owner,
        Transform = npc.Transform
    });

    private void Die(Npc npc)
    {
        npc.Hp = 0;
        npc.Events.OnDeath(npc, new OnDeathArgs { Killer = npc, Victim = npc });
        _manager.DoQueuedEvaluations();
    }

    private static void SetParentWorld(GameObject source, WorldInstance world) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(source, world);

    private sealed class PendingObjective(QuestComponentTemplate component) : QuestActTemplate(component)
    {
        public override bool CountsAsAnObjective => true;
        public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount) => currentObjectiveCount > 0;
    }

    private sealed class RemovalOnFinalize(QuestComponentTemplate component, Npc npc) : QuestActTemplate(component)
    {
        public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount) => true;

        public override void FinalizeAction(Quest quest, QuestAct questAct) => npc.Delete();
    }

    private sealed class TestNpc : Npc
    {
        public override void Hide() => IsVisible = false;
    }
}
