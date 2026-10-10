using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class QuestCompletionPersistenceTests
{
    [Fact]
    public void Reward_BeforeCommitFails_RestoresPreparedStateAndRetriesOnce()
    {
        using var graph = new CompletionGraph(4_100_244);
        var quest = graph.AddRewardAttempt();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        graph.Owner.Quests.CompletionPersistenceFailpoint = stage =>
        {
            if (stage == QuestCompletionPersistenceStage.BeforeCommit)
                throw new InvalidOperationException("Injected failure before quest commit.");
        };

        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Same(quest, graph.Owner.Quests.ActiveQuests[quest.TemplateId]);
        Assert.Equal(QuestStatus.Ready, quest.Status);
        Assert.False(graph.Owner.Quests.HasQuestCompleted(quest.TemplateId));
        Assert.Equal(0u, graph.Owner.Achievements.GetAmount(CompletionGraph.TypeAchievementId));
        Assert.Equal(0u, graph.Owner.Achievements.GetAmount(CompletionGraph.CategoryAchievementId));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, graph.CompletionPacketCount);
        graph.AssertStoredState(activeCount: 1, completed: false, typeCount: 0, categoryCount: 0);

        graph.Owner.Quests.CompletionPersistenceFailpoint = null;
        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Equal(1, callbacks);
        Assert.Equal(1, graph.CompletionPacketCount);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reward_ActualCommitReplyFails_ReconcilesStoredOutcomeWithoutAnotherIncrement(bool transactionReplyFails)
    {
        using var graph = new CompletionGraph(4_100_245);
        var quest = graph.AddRewardAttempt();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        if (transactionReplyFails)
        {
            graph.Owner.Quests.CommitQuestCompletionTransaction = transaction =>
            {
                transaction.Commit();
                throw new InvalidOperationException("Injected failure after the actual MySQL commit.");
            };
        }
        else
        {
            graph.Owner.Quests.CompletionPersistenceFailpoint = stage =>
            {
                if (stage == QuestCompletionPersistenceStage.AfterCommit)
                    throw new InvalidOperationException("Injected failure after quest commit.");
            };
        }

        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Empty(graph.Owner.Quests.ActiveQuests);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(1, callbacks);
        Assert.True(graph.Owner.Quests.HasQuestCompleted(quest.TemplateId));
        Assert.Equal(1, graph.CompletionPacketCount);
        Assert.Equal(1u, graph.Owner.Achievements.GetAmount(CompletionGraph.CategoryAchievementId));
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
    }

    [Fact]
    public void Reward_BeforeSuccessPacketFails_KeepsCommittedStateAndCannotRepeatProgress()
    {
        using var graph = new CompletionGraph(4_100_246);
        var quest = graph.AddRewardAttempt();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        graph.Owner.Quests.CompletionPersistenceFailpoint = stage =>
        {
            if (stage == QuestCompletionPersistenceStage.BeforePackets)
                throw new InvalidOperationException("Injected failure before quest success packets.");
        };

        Assert.Throws<InvalidOperationException>(quest.GoToNextStep);

        Assert.Empty(graph.Owner.Quests.ActiveQuests);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.True(graph.Owner.Quests.HasQuestCompleted(quest.TemplateId));
        Assert.Equal(0, callbacks);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
        Assert.Equal(0, graph.CompletionPacketCount);

        graph.Owner.Quests.CompletionPersistenceFailpoint = null;
        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Equal(0, callbacks);
        Assert.Equal(0, graph.CompletionPacketCount);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reward_MissingAcceptRowAndRepeatableBit_UnknownCommitStopsPersistence(bool commitActuallySucceeded)
    {
        using var graph = new CompletionGraph(4_100_247, repeatable: true);
        var first = graph.AddRewardAttempt();
        first.GoToNextStep();
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
        var second = graph.AddRewardAttempt(persist: false);
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        graph.Owner.Quests.CommitQuestCompletionTransaction = transaction =>
        {
            if (commitActuallySucceeded)
                transaction.Commit();
            else
                transaction.Rollback();
            throw new InvalidOperationException("Injected uncertain reply without a durable quest source.");
        };

        Assert.Throws<InvalidOperationException>(second.GoToNextStep);

        Assert.Same(second, graph.Owner.Quests.ActiveQuests[second.TemplateId]);
        Assert.Equal(QuestStatus.Ready, second.Status);
        Assert.True(graph.Owner.Quests.HasQuestCompleted(second.TemplateId));
        Assert.Equal(0, callbacks);
        Assert.Equal(1, graph.StopCalls);
        Assert.Equal(1, graph.CompletionPacketCount);
        Assert.Throws<InvalidOperationException>(second.GoToNextStep);
        Assert.False(graph.Save.DoSave());
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        Assert.Throws<InvalidOperationException>(() => graph.Owner.Quests.Save(connection, transaction));
        transaction.Rollback();
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: commitActuallySucceeded ? 2u : 1u, categoryCount: 1);
    }

    [Fact]
    public void Reward_StoredRepeatableAttemptCommitReplyFailsAfterRollback_RestoresStateAndRetriesOnce()
    {
        using var graph = new CompletionGraph(4_100_248, repeatable: true);
        var first = graph.AddRewardAttempt();
        first.GoToNextStep();
        var second = graph.AddRewardAttempt();
        var before = second.WriteData();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        graph.Owner.Quests.CommitQuestCompletionTransaction = transaction =>
        {
            transaction.Rollback();
            throw new InvalidOperationException("Injected uncertain reply after a known-source transaction abort.");
        };

        second.GoToNextStep();
        second.GoToNextStep();

        Assert.Same(second, graph.Owner.Quests.ActiveQuests[second.TemplateId]);
        Assert.Equal(QuestStatus.Ready, second.Status);
        Assert.Equal(before, graph.ReadStoredQuest().WriteData());
        Assert.Equal(0, callbacks);
        Assert.Equal(0, graph.StopCalls);
        Assert.Equal(1, graph.CompletionPacketCount);
        graph.AssertStoredState(activeCount: 1, completed: true, typeCount: 1, categoryCount: 1);

        graph.Owner.Quests.CommitQuestCompletionTransaction = transaction => transaction.Commit();
        second.GoToNextStep();
        second.GoToNextStep();
        first.GoToNextStep();

        Assert.Equal(1, callbacks);
        Assert.Equal(2, graph.CompletionPacketCount);
        Assert.Equal(0, graph.StopCalls);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 2, categoryCount: 1);
    }
}
