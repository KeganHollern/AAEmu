using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class CharacterQuestsTests
{
    [Test]
    public async Task TryStartQuest_FailedStart_ReusesRuntimeId()
    {
        var owner = new CharacterMock { Id = 7, Name = "Questor" };
        var quests = new CharacterQuests(owner);
        owner.Quests = quests;

        var template = new QuestTemplate { Id = 1_533 };
        var rewardComponent = new QuestComponentTemplate(template)
        {
            Id = 15_331,
            KindId = QuestComponentKind.Reward
        };
        template.Components.Add(rewardComponent.Id, rewardComponent);

        var quest = new Quest(
            template,
            owner,
            Mock.Of<IQuestManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
        var questIdManager = new QuestIdManager();
        questIdManager.Initialize(true);
        var runtimeId = questIdManager.GetNextId();
        questIdManager.ReleaseId(runtimeId);

        var result = quests.TryStartQuest(quest, questIdManager);

        await Assert.That(result).IsFalse();
        await Assert.That(quests.ActiveQuests).IsEmpty();
        await Assert.That(questIdManager.GetNextId()).IsEqualTo(runtimeId);
    }

    [Test]
    public async Task SetCompletedQuestFlag_FailedWriteRestoresStateForRetry()
    {
        var quests = new CharacterQuests(new CharacterMock { Id = 7, Name = "Questor" });

        quests.SetCompletedQuestFlag(
            2941,
            true,
            _ => false,
            out var failedPersisted,
            out var failedFirstCompletion);

        await Assert.That(failedPersisted).IsFalse();
        await Assert.That(failedFirstCompletion).IsTrue();
        await Assert.That(quests.IsQuestComplete(2941)).IsFalse();

        quests.SetCompletedQuestFlag(
            2941,
            true,
            _ => true,
            out var retryPersisted,
            out var retryFirstCompletion);

        await Assert.That(retryPersisted).IsTrue();
        await Assert.That(retryFirstCompletion).IsTrue();
        await Assert.That(quests.IsQuestComplete(2941)).IsTrue();

        quests.SetCompletedQuestFlag(
            2941,
            true,
            _ => true,
            out _,
            out var repeatedFirstCompletion);

        await Assert.That(repeatedFirstCompletion).IsFalse();
    }
}
