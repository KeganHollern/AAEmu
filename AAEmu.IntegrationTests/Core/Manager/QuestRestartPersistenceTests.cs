using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class QuestRestartPersistenceTests
{
    [Fact]
    public void RestartMainQuest_RealCommit_ReloadsFreshAttemptAndPreservesCompletionBlock()
    {
        const uint OwnerId = 4_100_278;
        var owner = CreateOwner(OwnerId);
        var failed = CreateFailedQuest(owner);
        Seed(failed);
        try
        {
            Assert.True(owner.Quests.RestartMainQuest(failed.TemplateId));

            var reloadedOwner = CreateOwner(OwnerId);
            var reloaded = CreateFailedQuest(reloadedOwner);
            reloadedOwner.Quests.ActiveQuests.Clear();
            ReadStoredQuest(reloaded);
            reloadedOwner.Quests.AddLoadedQuest(reloaded);
            Assert.Equal(987, reloaded.Id);
            Assert.Equal(QuestComponentKind.Start, reloaded.Step);
            Assert.Equal(QuestStatus.Progress, reloaded.Status);
            Assert.All(reloaded.Objectives, objective => Assert.Equal(0, objective));
            Assert.Empty(reloaded.AppliedSideEffectActIds);
            Assert.Equal(42u, reloaded.AcceptorId);
            Assert.Equal(QuestAcceptorType.Npc, reloaded.QuestAcceptorType);
            Assert.False(reloadedOwner.Quests.RestartMainQuest(reloaded.TemplateId));

            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM quests WHERE owner=@owner";
            command.Parameters.AddWithValue("@owner", OwnerId);
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
            command.CommandText = "SELECT data FROM completed_quests WHERE owner=@owner AND id=1";
            Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, (byte[])command.ExecuteScalar());
        }
        finally
        {
            Cleanup(OwnerId);
        }
    }

    [Fact]
    public void RestartMainQuest_RealWriteFails_PreservesFailedRowAndRetriesOnce()
    {
        const uint OwnerId = 4_100_279;
        const string Trigger = "quest_restart_failure_4100279";
        var owner = CreateOwner(OwnerId);
        var failed = CreateFailedQuest(owner);
        var before = failed.WriteData();
        Seed(failed);
        try
        {
            Execute($"CREATE TRIGGER {Trigger} BEFORE INSERT ON quests FOR EACH ROW BEGIN IF NEW.owner={OwnerId} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected quest restart failure'; END IF; END");
            Assert.False(owner.Quests.RestartMainQuest(failed.TemplateId));
            Assert.Same(failed, owner.Quests.ActiveQuests[failed.TemplateId]);

            var stored = CreateFailedQuest(CreateOwner(OwnerId));
            ReadStoredQuest(stored);
            Assert.Equal(QuestStatus.Failed, stored.Status);
            Assert.Equal(before, stored.WriteData());
            Execute($"DROP TRIGGER {Trigger}");

            Assert.True(owner.Quests.RestartMainQuest(failed.TemplateId));
            Assert.False(owner.Quests.RestartMainQuest(failed.TemplateId));
            ReadStoredQuest(stored);
            Assert.Equal(QuestComponentKind.Start, stored.Step);
            Assert.Equal(QuestStatus.Progress, stored.Status);
            Assert.All(stored.Objectives, objective => Assert.Equal(0, objective));
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {Trigger}");
            Cleanup(OwnerId);
        }
    }

    private static Character CreateOwner(uint ownerId)
    {
        var owner = new Character(null) { Id = ownerId, Name = "quest-restart-tester" };
        owner.Quests = new CharacterQuests(owner);
        return owner;
    }

    private static Quest CreateFailedQuest(Character owner)
    {
        var template = new QuestTemplate { Id = 101, DetailId = QuestDetail.Main, RestartOnFail = true };
        var start = new QuestComponentTemplate(template) { Id = 1011, KindId = QuestComponentKind.Start };
        template.Components.Add(start.Id, start);
        var quest = new Quest(template, owner, Mock.Of<IQuestManager>(), Mock.Of<ITaskManager>(),
            Mock.Of<ISkillManager>(), Mock.Of<IExpressTextManager>(), Mock.Of<IWorldManager>())
        {
            Id = 987,
            Status = QuestStatus.Failed,
            Step = QuestComponentKind.Fail,
            QuestAcceptorType = QuestAcceptorType.Npc,
            AcceptorId = 42,
            Objectives = [1, 2, 3, 4, 5]
        };
        quest.AppliedSideEffectActIds.Add(1012);
        owner.Quests.ActiveQuests.Add(template.Id, quest);
        return quest;
    }

    private static void Seed(Quest quest)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "REPLACE INTO quests(id,template_id,data,status,owner) VALUES(@id,@template,@data,@status,@owner)";
        command.Parameters.AddWithValue("@id", quest.Id);
        command.Parameters.AddWithValue("@template", quest.TemplateId);
        command.Parameters.AddWithValue("@data", quest.WriteData());
        command.Parameters.AddWithValue("@status", (byte)quest.Status);
        command.Parameters.AddWithValue("@owner", quest.Owner.Id);
        command.ExecuteNonQuery();
        command.CommandText = "REPLACE INTO completed_quests(id,data,owner) VALUES(1,X'0100000000000000',@owner)";
        command.ExecuteNonQuery();
    }

    private static void ReadStoredQuest(Quest quest)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,template_id,data,status FROM quests WHERE owner=@owner AND template_id=@template";
        command.Parameters.AddWithValue("@owner", quest.Owner.Id);
        command.Parameters.AddWithValue("@template", quest.TemplateId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        quest.Id = reader.GetUInt32("id");
        quest.TemplateId = reader.GetUInt32("template_id");
        quest.Status = (QuestStatus)reader.GetByte("status");
        quest.ReadData((byte[])reader["data"]);
        Assert.False(reader.Read());
    }

    private static void Cleanup(uint ownerId)
    {
        Execute($"DELETE FROM quests WHERE owner={ownerId}; DELETE FROM completed_quests WHERE owner={ownerId}");
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
