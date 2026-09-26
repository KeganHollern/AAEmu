using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class QuestDailyResetPersistenceTests
{
    [Fact]
    public void ResetDailyQuests_RealWrite_ReloadKeepsResetWithoutCharacterSave()
    {
        const uint OwnerId = 4_200_279;
        var owner = CreateOwner(OwnerId);
        try
        {
            Seed(owner);
            owner.Quests.ResetDailyQuests(false, GetTemplate);

            var reloaded = Reload(OwnerId);
            Assert.False(reloaded.Quests.IsQuestComplete(63));
            Assert.False(reloaded.Quests.IsQuestComplete(64));
            Assert.False(reloaded.Quests.IsQuestComplete(65));
            Assert.True(reloaded.Quests.IsQuestComplete(66));
        }
        finally
        {
            Execute($"DELETE FROM completed_quests WHERE owner={OwnerId}");
        }
    }

    [Fact]
    public void ResetDailyQuests_RealWriteFailure_KeepsFailedBlockAndRetries()
    {
        const uint OwnerId = 4_200_280;
        const string Trigger = "daily_reset_failure_4200280";
        var owner = CreateOwner(OwnerId);
        try
        {
            Seed(owner);
            Execute($"CREATE TRIGGER {Trigger} BEFORE INSERT ON completed_quests FOR EACH ROW BEGIN IF NEW.owner={OwnerId} AND NEW.id=1 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected daily reset failure'; END IF; END");
            owner.Quests.ResetDailyQuests(false, GetTemplate);

            Assert.False(owner.Quests.IsQuestComplete(63));
            Assert.True(owner.Quests.IsQuestComplete(64));
            Assert.True(owner.Quests.IsQuestComplete(65));
            var reloaded = Reload(OwnerId);
            Assert.False(reloaded.Quests.IsQuestComplete(63));
            Assert.True(reloaded.Quests.IsQuestComplete(64));
            Assert.True(reloaded.Quests.IsQuestComplete(65));
            Assert.True(reloaded.Quests.IsQuestComplete(66));

            Execute($"DROP TRIGGER {Trigger}");
            owner.Quests.ResetDailyQuests(false, GetTemplate);
            reloaded = Reload(OwnerId);
            Assert.False(reloaded.Quests.IsQuestComplete(64));
            Assert.False(reloaded.Quests.IsQuestComplete(65));
            Assert.True(reloaded.Quests.IsQuestComplete(66));
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {Trigger}");
            Execute($"DELETE FROM completed_quests WHERE owner={OwnerId}");
        }
    }

    private static QuestTemplate GetTemplate(uint id) => new()
    {
        Id = id, DetailId = id == 66 ? QuestDetail.Normal : QuestDetail.Daily
    };

    private static Character CreateOwner(uint id)
    {
        var owner = new Character(null) { Id = id, Name = "daily-reset-tester" };
        owner.Quests = new CharacterQuests(owner);
        return owner;
    }

    private static Character Reload(uint id)
    {
        var owner = CreateOwner(id);
        using var connection = MySQL.CreateConnection();
        owner.Quests.Load(connection);
        return owner;
    }

    private static void Seed(Character owner)
    {
        foreach (var id in new uint[] { 63, 64, 65, 66 })
        {
            owner.Quests.SetCompletedQuestFlag(id, true, out var persisted);
            Assert.True(persisted);
        }
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
