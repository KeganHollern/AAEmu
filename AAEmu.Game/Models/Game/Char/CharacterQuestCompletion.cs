using System.Collections;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Char;

internal enum QuestCompletionPersistenceStage
{
    BeforeCommit,
    AfterCommit,
    BeforePackets
}

public partial class CharacterQuests
{
    private bool _completionConsistencyFailed;

    internal Func<Quest, CompletedQuest, bool> QuestCompletionPersistenceOverride { get; set; }
    internal Action<QuestCompletionPersistenceStage> CompletionPersistenceFailpoint { get; set; }
    internal Action<MySqlTransaction> CommitQuestCompletionTransaction { get; set; } = transaction => transaction.Commit();

    internal bool TryCompleteQuest(Quest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_completionConsistencyFailed)
                throw new InvalidOperationException("Quest persistence stopped after an unconfirmed commit.");
            if (!ActiveQuests.TryGetValue(quest.TemplateId, out var active) || !ReferenceEquals(active, quest) ||
                !ReferenceEquals(quest.Owner, Owner) || quest.Step != QuestComponentKind.Reward)
                return false;

            var blockId = (ushort)(quest.TemplateId / 64);
            var blockIndex = (int)(quest.TemplateId % 64);
            var candidate = CompletedQuests.TryGetValue(blockId, out var previous)
                ? new CompletedQuest(blockId) { Body = new BitArray(previous.Body) }
                : new CompletedQuest(blockId);
            var firstCompletion = !candidate.Body[blockIndex];
            candidate.Body.Set(blockIndex, true);

            var achievements = Owner.Achievements;
            using var deferred = achievements?.BeginDeferredPersistence();
            List<AchievementProgressEvent> progressEvents =
            [
                new(CharRecordKind.CompleteQuestType, quest.TemplateId, 0, 1)
            ];
            if (firstCompletion)
                progressEvents.Add(new AchievementProgressEvent(CharRecordKind.CompleteQuestCategory, quest.Template.CategoryId, 0, 1));
            achievements?.Increment(progressEvents);

            var persisted = QuestCompletionPersistenceOverride != null
                ? QuestCompletionPersistenceOverride(quest, candidate)
                : FlushQuestCompletion(quest, candidate, deferred);
            if (!persisted)
            {
                quest.CompletionRetryPending = true;
                return false;
            }

            // Install committed state before any cleanup callback, event, or packet can throw.
            CompletedQuests[blockId] = candidate;
            quest.Status = QuestStatus.Completed;
            quest.SkipUpdatePackets();
            ActiveQuests.Remove(quest.TemplateId);
            _removed.RemoveAll(id => id == quest.TemplateId);
            quest.CompletionRetryPending = false;
            var notifications = deferred?.CreateCommittedNotifications();
            deferred?.Commit();

            quest.FinalizeCompletion();

            CompletionPersistenceFailpoint?.Invoke(QuestCompletionPersistenceStage.BeforePackets);
            achievements?.SendCommittedState(notifications);
            Owner.Events?.OnQuestComplete(Owner, new OnQuestCompleteArgs
            {
                QuestId = quest.TemplateId,
                Selected = quest.SelectedRewardIndex
            });
            var body = new byte[8];
            candidate.Body.CopyTo(body, 0);
            Owner.SendPacket(new SCQuestContextCompletedPacket(quest.TemplateId, body, 0));
            return true;
        }
    }

    private bool FlushQuestCompletion(Quest quest, CompletedQuest candidate, CharacterAchievements.DeferredPersistenceScope deferred)
    {
        var commitAttempted = false;
        var sourcePresent = false;
        try
        {
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT template_id FROM quests WHERE id=@id AND owner=@owner FOR UPDATE";
                command.Parameters.AddWithValue("@id", quest.Id);
                command.Parameters.AddWithValue("@owner", Owner.Id);
                var templateId = command.ExecuteScalar();
                if (templateId != null && Convert.ToUInt32(templateId) != quest.TemplateId)
                    throw new InvalidOperationException($"Quest attempt {quest.Id} does not match its durable source.");
                sourcePresent = templateId != null;

                command.CommandText = "DELETE FROM quests WHERE id=@id AND owner=@owner AND template_id=@template_id";
                command.Parameters.AddWithValue("@template_id", quest.TemplateId);
                if (command.ExecuteNonQuery() != (sourcePresent ? 1 : 0))
                    throw new InvalidOperationException($"Quest attempt {quest.Id} was not removed exactly once.");

                command.Parameters.Clear();
                command.CommandText = "REPLACE INTO completed_quests(`id`,`data`,`owner`) VALUES(@id,@data,@owner)";
                var body = new byte[8];
                candidate.Body.CopyTo(body, 0);
                command.Parameters.AddWithValue("@id", candidate.Id);
                command.Parameters.AddWithValue("@data", body);
                command.Parameters.AddWithValue("@owner", Owner.Id);
                command.ExecuteNonQuery();
                Owner.Achievements?.Save(connection, transaction);

                CompletionPersistenceFailpoint?.Invoke(QuestCompletionPersistenceStage.BeforeCommit);
                commitAttempted = true;
                CommitQuestCompletionTransaction(transaction);
                CompletionPersistenceFailpoint?.Invoke(QuestCompletionPersistenceStage.AfterCommit);
                return true;
            }
            catch when (!commitAttempted)
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception exception) when (!commitAttempted)
        {
            Logger.Warn(exception, "Quest completion aborted before commit for quest {QuestId}, attempt {AttemptId}, character {OwnerId}",
                quest.TemplateId, quest.Id, Owner.Id);
            return false;
        }
        catch (Exception exception)
        {
            try
            {
                // Some quests finish before the accept write, and a prior repeatable bit is not a receipt.
                if (!sourcePresent)
                    throw new InvalidOperationException($"Quest attempt {quest.Id} has no durable source to establish its commit outcome.");
                var committed = ReadCompletionOutcome(quest, candidate);
                Logger.Warn(exception, "Quest completion commit reply failed for quest {QuestId}, attempt {AttemptId}, character {OwnerId}. Durable commit: {Committed}",
                    quest.TemplateId, quest.Id, Owner.Id, committed);
                return committed;
            }
            catch (Exception recoveryException)
            {
                // Never restore and autosave a candidate whose commit outcome remains unknown.
                _completionConsistencyFailed = true;
                deferred?.PreservePreparedState();
                SaveManager.Instance.FailForConsistency(new AggregateException(exception, recoveryException));
                throw;
            }
        }
    }

    private bool ReadCompletionOutcome(Quest quest, CompletedQuest candidate)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // This lock waits for the original transaction to finish before deciding its outcome.
        command.CommandText = "SELECT template_id FROM quests WHERE id=@id AND owner=@owner FOR UPDATE";
        command.Parameters.AddWithValue("@id", quest.Id);
        command.Parameters.AddWithValue("@owner", Owner.Id);
        var templateId = command.ExecuteScalar();
        if (templateId != null)
        {
            if (Convert.ToUInt32(templateId) != quest.TemplateId)
                throw new InvalidOperationException($"Quest attempt {quest.Id} changed its durable template.");
            return false;
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT data FROM completed_quests WHERE id=@id AND owner=@owner";
        command.Parameters.AddWithValue("@id", candidate.Id);
        command.Parameters.AddWithValue("@owner", Owner.Id);
        var expected = new byte[8];
        candidate.Body.CopyTo(expected, 0);
        if (command.ExecuteScalar() is not byte[] body || !body.SequenceEqual(expected))
            throw new InvalidOperationException($"Quest attempt {quest.Id} disappeared without its completed block.");
        return true;
    }
}
