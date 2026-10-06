using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class QuestGuardPersistenceTests
{
    [Fact]
    public void TalkStartedEscort_CheckpointsBeforeCharacterSave_ThenFailsAfterRestart()
    {
        using var graph = new GuardGraph(extraObjective: true);
        graph.SaveCharacterQuests();
        Assert.Equal(0, graph.ReadStoredQuest().Objectives[0]);

        graph.Talk();

        var started = graph.ReadStoredQuest();
        Assert.Equal(QuestComponentKind.Progress, started.Step);
        Assert.Equal(QuestStatus.Progress, started.Status);
        Assert.Equal(1, started.Objectives[0]);

        var reloaded = graph.Reload();
        AssertFailed(reloaded);
        AssertFailed(graph.ReadStoredQuest());
        AssertFailed(graph.Reload());
    }

    [Fact]
    public void AcceptorStartedEscort_WithZeroTalkProgress_FailsAfterRestart()
    {
        using var graph = new GuardGraph(startGuard: true);
        graph.SaveCharacterQuests();
        var accepted = graph.ReadStoredQuest();
        Assert.Equal(QuestComponentKind.Start, accepted.Step);
        Assert.Equal(QuestAcceptorType.Npc, accepted.QuestAcceptorType);
        Assert.All(accepted.Objectives, objective => Assert.Equal(0, objective));

        AssertFailed(graph.Reload());
        AssertFailed(graph.ReadStoredQuest());
    }

    [Fact]
    public void UnstartedEscort_RestartAndDisconnectKeepItsSavedProgressState()
    {
        using var graph = new GuardGraph();
        graph.SaveCharacterQuests();

        var reloaded = graph.Reload();
        Assert.Equal(QuestComponentKind.Progress, reloaded.Step);
        Assert.Equal(QuestStatus.Progress, reloaded.Status);
        Assert.All(reloaded.Objectives, objective => Assert.Equal(0, objective));
        reloaded.Owner.Quests.PrepareEscortQuestsForDisconnect();
        ((CharacterEvents)reloaded.Owner.Events).OnDisconnect(reloaded.Owner, new OnDisconnectArgs());

        var stored = graph.ReadStoredQuest();
        Assert.Equal(QuestComponentKind.Progress, stored.Step);
        Assert.Equal(QuestStatus.Progress, stored.Status);
        Assert.All(stored.Objectives, objective => Assert.Equal(0, objective));
    }

    [Fact]
    public void UnfinishedEscort_DisconnectCheckpointsFailureBeforeCharacterSave()
    {
        using var graph = new GuardGraph(extraObjective: true);
        graph.SaveCharacterQuests();
        graph.Talk();

        graph.Owner.Quests.PrepareEscortQuestsForDisconnect();
        AssertFailed(graph.Quest);
        AssertFailed(graph.ReadStoredQuest());

        graph.Owner.Events.OnDisconnect(graph.Owner, new OnDisconnectArgs());
        graph.Owner.Quests.PrepareEscortQuestsForDisconnect();
        AssertFailed(graph.Reload());
    }

    [Fact]
    public void CapturedTalkAfterDisconnect_CannotPersistAFalseEscortStart()
    {
        using var graph = new GuardGraph(extraObjective: true);
        graph.SaveCharacterQuests();
        graph.Owner.Quests.PrepareEscortQuestsForDisconnect();

        // A captured event delegate can run after its subscription is removed.
        graph.Talk();
        graph.SaveCharacterQuests();

        var reloaded = graph.Reload();
        Assert.Equal(QuestComponentKind.Progress, reloaded.Step);
        Assert.Equal(QuestStatus.Progress, reloaded.Status);
        Assert.All(reloaded.Objectives, objective => Assert.Equal(0, objective));
        Assert.Equal(0, graph.ReadStoredQuest().Objectives[0]);
    }

    [Fact]
    public void GuardDeath_CheckpointsFailureBeforeCharacterSave()
    {
        using var graph = new GuardGraph(extraObjective: true);
        graph.SaveCharacterQuests();
        graph.Talk();

        graph.Die();

        AssertFailed(graph.Quest);
        AssertFailed(graph.ReadStoredQuest());
        AssertFailed(graph.Reload());
    }

    [Fact]
    public void CompletedEscort_TalkCheckpointsReadyBeforeCharacterSave()
    {
        using var graph = new GuardGraph();
        graph.SaveCharacterQuests();
        graph.Talk();

        AssertReady(graph.Quest);
        AssertReady(graph.ReadStoredQuest());

        graph.Owner.Quests.PrepareEscortQuestsForDisconnect();
        graph.Owner.Events.OnDisconnect(graph.Owner, new OnDisconnectArgs());
        graph.Die();
        AssertReady(graph.Reload());
        AssertReady(graph.ReadStoredQuest());
    }

    [Fact]
    public void CompletedObjectivesAtDisconnect_CheckpointReadyInsteadOfFailure()
    {
        using var graph = new GuardGraph(extraObjective: true);
        graph.SaveCharacterQuests();
        graph.Talk();
        Assert.Equal(QuestComponentKind.Progress, graph.Quest.Step);
        // An objective event can update its counter before queued evaluation runs.
        graph.Quest.Objectives[1] = 1;

        graph.Owner.Quests.PrepareEscortQuestsForDisconnect();

        AssertReady(graph.Quest);
        AssertReady(graph.ReadStoredQuest());
        AssertReady(graph.Reload());
    }

    [Fact]
    public void CompletedEscort_WithoutReadyComponent_CheckpointsRewardBeforeCharacterSave()
    {
        using var graph = new GuardGraph(hasReadyStep: false);
        graph.SaveCharacterQuests();

        graph.Talk();

        Assert.Equal(QuestComponentKind.Reward, graph.Quest.Step);
        var stored = graph.ReadStoredQuest();
        Assert.Equal(QuestComponentKind.Reward, stored.Step);
        Assert.Equal(QuestStatus.Ready, stored.Status);
        Assert.Equal(1, stored.Objectives[0]);
        graph.Owner.Quests.PrepareEscortQuestsForDisconnect();
        graph.Owner.Events.OnDisconnect(graph.Owner, new OnDisconnectArgs());

        var reloaded = graph.Reload();
        Assert.Equal(QuestComponentKind.Reward, reloaded.Step);
        Assert.Equal(QuestStatus.Ready, reloaded.Status);
        Assert.Equal(1, reloaded.Objectives[0]);
        Assert.Equal(QuestComponentKind.Reward, graph.ReadStoredQuest().Step);
    }

    private static void AssertFailed(Quest quest)
    {
        Assert.Equal(QuestComponentKind.Fail, quest.Step);
        Assert.Equal(QuestStatus.Failed, quest.Status);
    }

    private static void AssertReady(Quest quest)
    {
        Assert.Equal(QuestComponentKind.Ready, quest.Step);
        Assert.Equal(QuestStatus.Ready, quest.Status);
        Assert.Equal(1, quest.Objectives[0]);
    }

    private sealed class GuardGraph : IDisposable
    {
        private const uint OwnerId = 6_460_272;
        private const uint QuestId = 3656;
        private const uint GuardTemplateId = 9846;
        private readonly QuestManager _manager = new(Mock.Of<ITaskManager>(), Mock.Of<IZoneManager>());
        private readonly WorldInstance _world = new(new WorldTemplate { Id = 1 }, 0, true, 1);
        private readonly QuestTemplate _template = new() { Id = QuestId };
        private readonly List<Quest> _quests = [];
        private readonly QuestAct _talk;
        private readonly Npc _guard;

        internal Character Owner { get; }
        internal Quest Quest { get; }

        internal GuardGraph(bool startGuard = false, bool extraObjective = false, bool hasReadyStep = true)
        {
            Owner = CreateOwner();
            _guard = new Npc { ObjId = 81, TemplateId = GuardTemplateId, Template = new NpcTemplate { Scale = 1 }, Hp = 100 };
            SetWorld(_guard, _world);
            Owner.CurrentTarget = _guard;
            var start = new QuestComponentTemplate(_template) { Id = 15272, KindId = QuestComponentKind.Start };
            var progress = new QuestComponentTemplate(_template) { Id = 15273, KindId = QuestComponentKind.Progress };
            var report = new QuestComponentTemplate(_template)
            {
                Id = 15274,
                KindId = hasReadyStep ? QuestComponentKind.Ready : QuestComponentKind.Reward
            };
            progress.ActTemplates.Add(new QuestActObjTalk(progress)
            {
                ActId = 20950,
                NpcId = GuardTemplateId,
                Count = 1,
                ThisComponentObjectiveIndex = 0
            });
            if (extraObjective)
            {
                progress.ActTemplates.Add(new QuestActObjTalk(progress)
                {
                    ActId = 20952,
                    NpcId = 9999,
                    Count = 1,
                    ThisComponentObjectiveIndex = 1
                });
            }
            var guardComponent = startGuard ? start : progress;
            guardComponent.ActTemplates.Add(new QuestActCheckGuard(guardComponent) { ActId = 20951, NpcId = GuardTemplateId });
            _template.Components.Add(start.Id, start);
            _template.Components.Add(progress.Id, progress);
            _template.Components.Add(report.Id, report);
            Quest = CreateQuest(Owner);
            Quest.QuestAcceptorType = startGuard ? QuestAcceptorType.Npc : QuestAcceptorType.Unknown;
            Quest.AcceptorId = startGuard ? GuardTemplateId : 0;
            Owner.Quests.ActiveQuests.Add(QuestId, Quest);
            Quest.Step = startGuard ? QuestComponentKind.Start : QuestComponentKind.Progress;
            _talk = Quest.QuestSteps[QuestComponentKind.Progress].Components[progress.Id].Acts[0];
        }

        internal void SaveCharacterQuests()
        {
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            Owner.Quests.Save(connection, transaction);
            transaction.Commit();
        }

        internal void Talk() => _talk.OnTalkMade(Owner, new OnTalkMadeArgs
        {
            QuestId = QuestId,
            NpcId = GuardTemplateId,
            SourcePlayer = Owner,
            Transform = _guard.Transform
        });

        internal void Die()
        {
            _guard.Hp = 0;
            _guard.Events.OnDeath(_guard, new OnDeathArgs { Killer = _guard, Victim = _guard });
            _manager.DoQueuedEvaluations();
        }

        internal Quest Reload()
        {
            var quest = ReadStoredQuest();
            quest.Owner.Quests.AddLoadedQuest(quest);
            return quest;
        }

        internal Quest ReadStoredQuest()
        {
            var quest = CreateQuest(CreateOwner());
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,template_id,data,status FROM quests WHERE owner=@owner AND template_id=@template";
            command.Parameters.AddWithValue("@owner", OwnerId);
            command.Parameters.AddWithValue("@template", QuestId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            quest.Id = reader.GetUInt32("id");
            quest.TemplateId = reader.GetUInt32("template_id");
            quest.Status = (QuestStatus)reader.GetByte("status");
            quest.ReadData((byte[])reader["data"]);
            Assert.False(reader.Read());
            return quest;
        }

        private Character CreateOwner()
        {
            var owner = new Character(null) { Id = OwnerId, ObjId = 70, Name = "guard-persistence-tester" };
            owner.Quests = new CharacterQuests(owner, new GameScheduleManager(null, TimeProvider.System));
            SetWorld(owner, _world);
            return owner;
        }

        private Quest CreateQuest(Character owner)
        {
            var quest = new Quest(_template, owner, _manager, Mock.Of<ITaskManager>(), Mock.Of<ISkillManager>(),
                Mock.Of<IExpressTextManager>(), Mock.Of<IWorldManager>())
            { Id = 987, Status = QuestStatus.Progress };
            _quests.Add(quest);
            return quest;
        }

        public void Dispose()
        {
            foreach (var quest in _quests)
                quest.FinalizeQuestActs();
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM quests WHERE owner=@owner; DELETE FROM completed_quests WHERE owner=@owner";
            command.Parameters.AddWithValue("@owner", OwnerId);
            command.ExecuteNonQuery();
        }

        private static void SetWorld(GameObject source, WorldInstance world) =>
            typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(source, world);
    }
}
