using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Tasks.Quests;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Tasks.Quests;

public class QuestDailyResetTaskTests
{
    [Test]
    public void Execute_MultipleCharacters_UsesAccountIdsAndOneUtcDay()
    {
        var firstCharacter = CreateCharacter(101, 7);
        var secondCharacter = CreateCharacter(102, 7);
        var otherAccountCharacter = CreateCharacter(103, 8);
        var worldManager = Mock.Of<IWorldManager>();
        worldManager.GetAllCharacters().Returns(
            [firstCharacter, secondCharacter, otherAccountCharacter]);
        var timedRewardsManager = Mock.Of<ITimedRewardsManager>();
        var rewardDate = new DateOnly(2026, 8, 31);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(
            rewardDate,
            new TimeOnly(23, 59),
            TimeSpan.Zero));
        var task = new QuestDailyResetTask(
            worldManager.Object,
            timedRewardsManager.Object,
            timeProvider);

        task.Execute();

        worldManager.GetAllCharacters().WasCalled(Times.Once);
        timedRewardsManager.DoDailyAccountLogin(7, rewardDate).WasCalled(Times.Exactly(2));
        timedRewardsManager.DoDailyAccountLogin(8, rewardDate).WasCalled(Times.Once);
        timedRewardsManager.DoDailyAccountLogin(101, rewardDate).WasCalled(Times.Never);
        timedRewardsManager.DoDailyAccountLogin(102, rewardDate).WasCalled(Times.Never);
        timedRewardsManager.DoDailyAccountLogin(103, rewardDate).WasCalled(Times.Never);
    }

    private static CharacterMock CreateCharacter(uint characterId, uint accountId)
    {
        var character = new CharacterMock
        {
            Id = characterId,
            AccountId = accountId
        };
        character.Quests = new CharacterQuests(character);
        return character;
    }
}
