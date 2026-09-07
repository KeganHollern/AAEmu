using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestCompleteObjectiveTests
{
    private const uint ParentQuestId = 5_814;
    private const uint UnrelatedQuestId = 9_999;
    private const uint ReportNpcId = 8_141;

    private static readonly uint[] s_targetQuestIds = [5_815, 5_816, 5_817, 5_818, 5_819];
    private static readonly FieldInfo s_questManagerInstanceField = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private IServiceProvider _previousServiceProvider;
    private QuestManager _previousQuestManager;
    private ServiceProvider _testServiceProvider;
    private QuestManager _manager;
    private bool _previousDebugInfo;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        _previousQuestManager = (QuestManager)s_questManagerInstanceField.GetValue(null);
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        _manager = CreateManager();

        var services = new ServiceCollection();
        services.AddSingleton(_manager);
        _testServiceProvider = services.BuildServiceProvider();

        s_questManagerInstanceField.SetValue(null, null);
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        s_questManagerInstanceField.SetValue(null, _previousQuestManager);
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task QuestCompletion_MultipleTargets_AdvancesParentOnlyAfterFinalTarget()
    {
        var owner = CreateOwner();
        var parent = CreateParentQuest(owner, _manager);
        owner.Quests.ActiveQuests.Add(parent.TemplateId, parent);
        parent.QuestInitialized();
        _manager.DoQueuedEvaluations();

        var unrelatedCompleted = CompleteTargetQuest(owner, _manager, UnrelatedQuestId);
        _manager.DoQueuedEvaluations();

        await Assert.That(unrelatedCompleted).IsTrue();
        await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)).IsEquivalentTo([0, 0, 0, 0, 0]);
        await Assert.That(parent.Step).IsEqualTo(QuestComponentKind.Progress);

        for (var i = 0; i < s_targetQuestIds.Length - 1; i++)
        {
            var targetCompleted = CompleteTargetQuest(owner, _manager, s_targetQuestIds[i]);
            _manager.DoQueuedEvaluations();

            await Assert.That(targetCompleted).IsTrue();
            await Assert.That(owner.Quests.HasQuestCompleted(s_targetQuestIds[i])).IsTrue();
            await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)[i]).IsEqualTo(1);
            await Assert.That(parent.Step).IsEqualTo(QuestComponentKind.Progress);
        }

        var finalTargetCompleted = CompleteTargetQuest(owner, _manager, s_targetQuestIds[^1]);
        _manager.DoQueuedEvaluations();

        await Assert.That(finalTargetCompleted).IsTrue();
        await Assert.That(owner.Quests.HasQuestCompleted(s_targetQuestIds[^1])).IsTrue();
        await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)).IsEquivalentTo([1, 1, 1, 1, 1]);
        await Assert.That(parent.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task Relog_StaleObjectives_RehydratesCompletedTargetsAndReceivesNextCompletion()
    {
        var firstSessionOwner = CreateOwner();
        var firstSessionParent = CreateParentQuest(firstSessionOwner, _manager);
        var persistedParentData = firstSessionParent.WriteData();
        firstSessionParent.Step = QuestComponentKind.Invalid;

        var owner = CreateOwner();
        for (var i = 0; i < s_targetQuestIds.Length - 1; i++)
            SetCompletedQuestFlag(owner, s_targetQuestIds[i]);

        var parent = CreateParentQuest(owner, _manager, false);
        parent.ReadData(persistedParentData);
        owner.Quests.ActiveQuests.Add(parent.TemplateId, parent);
        parent.QuestInitialized();
        _manager.DoQueuedEvaluations();

        for (var i = 0; i < s_targetQuestIds.Length - 1; i++)
            await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)[i]).IsEqualTo(1);
        await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)[^1]).IsEqualTo(0);
        await Assert.That(parent.Step).IsEqualTo(QuestComponentKind.Progress);

        var finalTargetCompleted = CompleteTargetQuest(owner, _manager, s_targetQuestIds[^1]);
        _manager.DoQueuedEvaluations();

        await Assert.That(finalTargetCompleted).IsTrue();
        await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)).IsEquivalentTo([1, 1, 1, 1, 1]);
        await Assert.That(parent.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task QuestCompletion_FailedPersistence_DoesNotAdvanceParent()
    {
        var owner = CreateOwner(false);
        var parent = CreateParentQuest(owner, _manager);
        owner.Quests.ActiveQuests.Add(parent.TemplateId, parent);
        parent.QuestInitialized();
        _manager.DoQueuedEvaluations();

        var rewardStepRan = CompleteTargetQuest(owner, _manager, s_targetQuestIds[0]);
        _manager.DoQueuedEvaluations();

        await Assert.That(rewardStepRan).IsTrue();
        await Assert.That(owner.Quests.HasQuestCompleted(s_targetQuestIds[0])).IsFalse();
        await Assert.That(owner.Quests.HasQuest(s_targetQuestIds[0])).IsTrue();
        await Assert.That(parent.GetObjectives(QuestComponentKind.Progress)).IsEquivalentTo([0, 0, 0, 0, 0]);
        await Assert.That(parent.Step).IsEqualTo(QuestComponentKind.Progress);
    }

    private static QuestManager CreateManager()
    {
        return new QuestManager(
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IZoneManager>().Object);
    }

    private static CharacterMock CreateOwner(bool persistCompletions = true)
    {
        var owner = new CharacterMock
        {
            Id = 7,
            ObjId = 70,
            Name = "Questor"
        };
        owner.Quests = new CharacterQuests(owner, _ => persistCompletions, _ => { });
        return owner;
    }

    private static Quest CreateParentQuest(CharacterMock owner, QuestManager manager, bool activate = true)
    {
        var template = new QuestTemplate { Id = ParentQuestId };
        for (byte i = 0; i < s_targetQuestIds.Length; i++)
            AddCompleteQuestObjective(template, s_targetQuestIds[i], i);

        var readyComponent = new QuestComponentTemplate(template)
        {
            Id = ParentQuestId * 100 + 99,
            KindId = QuestComponentKind.Ready
        };
        readyComponent.ActTemplates.Add(new QuestActConReportNpc(readyComponent)
        {
            ActId = ParentQuestId * 100 + 100,
            NpcId = ReportNpcId
        });
        template.Components.Add(readyComponent.Id, readyComponent);

        var quest = CreateQuest(template, owner, manager);
        if (activate)
            quest.Step = QuestComponentKind.Progress;
        return quest;
    }

    private static void AddCompleteQuestObjective(QuestTemplate template, uint targetQuestId, byte objectiveIndex)
    {
        var component = new QuestComponentTemplate(template)
        {
            Id = ParentQuestId * 100 + objectiveIndex,
            KindId = QuestComponentKind.Progress
        };
        component.ActTemplates.Add(new QuestActObjCompleteQuest(component)
        {
            ActId = ParentQuestId * 100 + objectiveIndex,
            QuestId = targetQuestId,
            Count = 1,
            ThisComponentObjectiveIndex = objectiveIndex
        });
        template.Components.Add(component.Id, component);
    }

    private static bool CompleteTargetQuest(CharacterMock owner, QuestManager manager, uint questId)
    {
        var template = new QuestTemplate { Id = questId };
        var rewardComponent = new QuestComponentTemplate(template)
        {
            Id = questId * 100,
            KindId = QuestComponentKind.Reward
        };
        template.Components.Add(rewardComponent.Id, rewardComponent);

        var target = CreateQuest(template, owner, manager);
        target.Step = QuestComponentKind.Reward;
        owner.Quests.ActiveQuests.Add(questId, target);
        return target.RunCurrentStep();
    }

    private static Quest CreateQuest(QuestTemplate template, CharacterMock owner, QuestManager manager)
    {
        return new Quest(
            template,
            owner,
            manager,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
    }

    private static void SetCompletedQuestFlag(CharacterMock owner, uint questId)
    {
        owner.Quests.SetCompletedQuestFlag(questId, true, out var persisted);
        if (!persisted)
            throw new InvalidOperationException($"Failed to arrange completed quest {questId}");
    }
}
