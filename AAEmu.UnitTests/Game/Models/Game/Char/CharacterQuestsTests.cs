using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class CharacterQuestsTests
{
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
