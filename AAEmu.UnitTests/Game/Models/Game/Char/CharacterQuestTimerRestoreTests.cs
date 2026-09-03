using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class CharacterQuestTimerRestoreTests
{
    private const uint OwnerId = 7;
    private const uint QuestId = 101;
    private const int TimerLimit = 180_000;

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
    [Arguments(QuestComponentKind.Start)]
    [Arguments(QuestComponentKind.Progress)]
    public async Task RestoreLoadedTimedQuest_BeforeDeadline_RestoresPersistedDeadline(
        QuestComponentKind timerStep)
    {
        var owner = CreateOwner();
        var quest = CreateTimedQuest(owner.Object, timerStep);
        var deadline = WholeSecondUtcNow().AddMinutes(2);
        quest.ReadData(CreateQuestData(deadline));

        owner.Object.Quests.AddLoadedQuest(quest);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.Time).IsEqualTo(deadline);
        await Assert.That(HasTimer()).IsTrue();

        var timeoutTask = _questManager.QuestTimeoutTask[OwnerId][QuestId];
        await Assert.That(timeoutTask.TriggerTime).IsEqualTo(deadline);

        timeoutTask.Execute();

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(HasTimer()).IsFalse();
        await Assert.That(timeoutTask.Cancelled).IsTrue();
    }

    [Test]
    [Arguments(QuestComponentKind.Start)]
    [Arguments(QuestComponentKind.Progress)]
    public async Task RestoreLoadedTimedQuest_AfterDeadline_FailsImmediately(QuestComponentKind timerStep)
    {
        var owner = CreateOwner();
        var quest = CreateTimedQuest(owner.Object, timerStep);
        var deadline = WholeSecondUtcNow().AddSeconds(-30);
        quest.ReadData(CreateQuestData(deadline));

        owner.Object.Quests.AddLoadedQuest(quest);

        await Assert.That(quest.Time).IsEqualTo(deadline);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(HasTimer()).IsFalse();
    }

    [Test]
    [Arguments(QuestComponentKind.Start)]
    [Arguments(QuestComponentKind.Progress)]
    public async Task RestoreLoadedTimedQuest_WithLegacyMaximumDeadline_FailsImmediately(
        QuestComponentKind timerStep)
    {
        var owner = CreateOwner();
        var quest = CreateTimedQuest(owner.Object, timerStep);
        var deadline = DateTime.MaxValue;
        quest.ReadData(CreateQuestData(deadline));

        owner.Object.Quests.AddLoadedQuest(quest);

        await Assert.That(quest.Time).IsEqualTo(deadline);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
        await Assert.That(HasTimer()).IsFalse();
    }

    [Test]
    public async Task RestoreLoadedState_BeforeStepSideEffects_ExposesAllPersistedFields()
    {
        var owner = CreateOwner();
        var template = new QuestTemplate { Id = QuestId };
        var component = new QuestComponentTemplate(template)
        {
            Id = 501,
            KindId = QuestComponentKind.Progress
        };
        var recordingAct = new RecordingQuestAct(component) { ActId = 502 };
        component.ActTemplates.Add(recordingAct);
        template.Components.Add(component.Id, component);

        var quest = CreateQuest(owner.Object, template);
        quest.Status = QuestStatus.Progress;
        var deadline = WholeSecondUtcNow().AddMinutes(5);
        quest.ReadData(CreateQuestData(deadline));

        await Assert.That(recordingAct.Snapshot).IsNull();

        owner.Object.Quests.AddLoadedQuest(quest);

        await Assert.That(recordingAct.Snapshot).IsNotNull();
        await Assert.That(recordingAct.Snapshot!.Objectives).IsEquivalentTo(new[] { 1, 2, 3, 4, 5 });
        await Assert.That(recordingAct.Snapshot.Status).IsEqualTo(QuestStatus.Progress);
        await Assert.That(recordingAct.Snapshot.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(recordingAct.Snapshot.AcceptorType).IsEqualTo(QuestAcceptorType.Npc);
        await Assert.That(recordingAct.Snapshot.ComponentId).IsEqualTo(123u);
        await Assert.That(recordingAct.Snapshot.AcceptorId).IsEqualTo(456u);
        await Assert.That(recordingAct.Snapshot.Deadline).IsEqualTo(deadline);
        await Assert.That(recordingAct.Snapshot.WasRegistered).IsTrue();
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

    private Quest CreateTimedQuest(ICharacter owner, QuestComponentKind timerStep)
    {
        var template = new QuestTemplate { Id = QuestId };
        AddComponent(template, QuestComponentKind.Start, 201);
        var progressComponent = AddComponent(template, QuestComponentKind.Progress, 301);
        var timerComponent = timerStep == QuestComponentKind.Start
            ? template.Components[201]
            : progressComponent;
        timerComponent.ActTemplates.Add(new QuestActCheckTimer(timerComponent)
        {
            ActId = 401,
            LimitTime = TimerLimit
        });

        return CreateQuest(owner, template);
    }

    private Quest CreateQuest(ICharacter owner, QuestTemplate template)
    {
        return new Quest(
            template,
            owner,
            _questManager,
            _taskManager,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
    }

    private static QuestComponentTemplate AddComponent(
        QuestTemplate template,
        QuestComponentKind step,
        uint componentId)
    {
        var component = new QuestComponentTemplate(template)
        {
            Id = componentId,
            KindId = step
        };
        template.Components.Add(component.Id, component);
        return component;
    }

    private static byte[] CreateQuestData(DateTime deadline)
    {
        var stream = new PacketStream();
        for (var objective = 1; objective <= 5; objective++)
            stream.Write(objective);
        stream.Write((byte)QuestComponentKind.Progress);
        stream.Write((byte)QuestAcceptorType.Npc);
        stream.Write(123u);
        stream.Write(456u);
        stream.Write(deadline);
        return stream.GetBytes();
    }

    private static DateTime WholeSecondUtcNow()
    {
        return DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
    }

    private bool HasTimer()
    {
        return _questManager.QuestTimeoutTask.TryGetValue(OwnerId, out var timers) &&
               timers.ContainsKey(QuestId);
    }

    private sealed class RecordingQuestAct(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
    {
        public QuestStateSnapshot Snapshot { get; private set; }

        public override void InitializeAction(Quest quest, QuestAct questAct)
        {
            Snapshot = new QuestStateSnapshot(
                quest.Objectives.ToArray(),
                quest.Status,
                quest.Step,
                quest.QuestAcceptorType,
                quest.ComponentId,
                quest.AcceptorId,
                quest.Time,
                quest.Owner.Quests.ActiveQuests.ContainsKey(quest.TemplateId));
        }
    }

    private sealed record QuestStateSnapshot(
        int[] Objectives,
        QuestStatus Status,
        QuestComponentKind Step,
        QuestAcceptorType AcceptorType,
        uint ComponentId,
        uint AcceptorId,
        DateTime Deadline,
        bool WasRegistered);
}
