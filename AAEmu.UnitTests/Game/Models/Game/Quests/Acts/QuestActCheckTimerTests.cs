using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

[NotInParallel]
public sealed class QuestActCheckTimerTests
{
    private const uint OwnerId = 7;
    private const uint FirstQuestId = 101;
    private const uint SecondQuestId = 102;

    private static readonly FieldInfo s_questManagerInstanceField = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_taskManagerInstanceField = typeof(Singleton<TaskManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private IServiceProvider _previousServiceProvider;
    private QuestManager _previousQuestManager;
    private TaskManager _previousTaskManager;
    private ServiceProvider _testServiceProvider;
    private QuestManager _questManager;
    private TaskManager _taskManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        _previousQuestManager = (QuestManager)s_questManagerInstanceField.GetValue(null);
        _previousTaskManager = (TaskManager)s_taskManagerInstanceField.GetValue(null);

        _taskManager = new TaskManager(Mock.Of<ITickManager>().Object);
        _questManager = new QuestManager(_taskManager, Mock.Of<IZoneManager>().Object);

        var services = new ServiceCollection();
        services.AddSingleton(_questManager);
        services.AddSingleton(_taskManager);
        _testServiceProvider = services.BuildServiceProvider();

        s_questManagerInstanceField.SetValue(null, null);
        s_taskManagerInstanceField.SetValue(null, null);
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        s_questManagerInstanceField.SetValue(null, _previousQuestManager);
        s_taskManagerInstanceField.SetValue(null, _previousTaskManager);
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task TimerExpired_TwoConcurrentTimers_FailsOnlyMatchingQuest()
    {
        var owner = CreateOwner();
        var firstQuest = AddTimedQuest(owner, FirstQuestId);
        var secondQuest = AddTimedQuest(owner, SecondQuestId);

        await Assert.That(HasTimer(FirstQuestId)).IsTrue();
        await Assert.That(HasTimer(SecondQuestId)).IsTrue();

        _questManager.OnTimerExpired(owner.Object, FirstQuestId);

        await Assert.That(firstQuest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(secondQuest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(HasTimer(FirstQuestId)).IsFalse();
        await Assert.That(HasTimer(SecondQuestId)).IsTrue();
    }

    [Test]
    [Arguments(QuestComponentKind.Ready)]
    [Arguments(QuestComponentKind.Fail)]
    [Arguments(QuestComponentKind.Drop)]
    [Arguments(QuestComponentKind.Reward)]
    public async Task QuestStepChanged_UnrelatedTerminalTransition_PreservesOtherTimer(QuestComponentKind terminalStep)
    {
        var owner = CreateOwner();
        var firstQuest = AddTimedQuest(owner, FirstQuestId);
        var secondQuest = AddTimedQuest(owner, SecondQuestId);

        await Assert.That(HasTimer(FirstQuestId)).IsTrue();
        await Assert.That(HasTimer(SecondQuestId)).IsTrue();

        firstQuest.Step = terminalStep;

        await Assert.That(firstQuest.Step).IsEqualTo(terminalStep);
        await Assert.That(secondQuest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(HasTimer(FirstQuestId)).IsFalse();
        await Assert.That(HasTimer(SecondQuestId)).IsTrue();

        _questManager.OnTimerExpired(owner.Object, SecondQuestId);

        await Assert.That(secondQuest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(HasTimer(SecondQuestId)).IsFalse();
    }

    private static Mock<ICharacter> CreateOwner()
    {
        var owner = Mock.Of<ICharacter>();
        owner.Id.Returns(OwnerId);
        owner.Name.Returns("Questor");
        owner.Events.Returns(new UnitEvents());
        owner.Quests.Returns(new CharacterQuests(new CharacterMock()));
        return owner;
    }

    private Quest AddTimedQuest(Mock<ICharacter> owner, uint questId)
    {
        var template = new QuestTemplate { Id = questId };
        var progressComponent = new QuestComponentTemplate(template)
        {
            Id = questId * 10 + 1,
            KindId = QuestComponentKind.Progress
        };
        progressComponent.ActTemplates.Add(new QuestActCheckTimer(progressComponent)
        {
            ActId = questId * 10 + 2,
            LimitTime = 60_000
        });
        template.Components.Add(progressComponent.Id, progressComponent);

        var quest = new Quest(
            template,
            owner.Object,
            _questManager,
            _taskManager,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
        owner.Object.Quests.ActiveQuests.Add(questId, quest);
        quest.Step = QuestComponentKind.Progress;
        return quest;
    }

    private bool HasTimer(uint questId)
    {
        return _questManager.QuestTimeoutTask.TryGetValue(OwnerId, out var timers) && timers.ContainsKey(questId);
    }
}
