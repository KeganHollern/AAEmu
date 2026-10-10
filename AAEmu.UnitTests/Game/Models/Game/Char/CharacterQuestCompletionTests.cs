using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class CharacterQuestCompletionTests
{
    [Before(Test)]
    public void InitializeQuestIds()
    {
        QuestIdManager.Instance.Initialize();
    }

    [Test]
    public async Task TryCompleteQuest_AbortedWrite_RestoresProgressAndRetriesOnce()
    {
        using var data = CreateAchievementData();
        var session = new RecordingSession();
        var owner = CreateOwner(data, session);
        var quest = CreateQuest(owner, 987);
        var before = quest.WriteData();
        var attempts = 0;
        var committed = false;
        var observedUncommittedState = false;
        owner.Quests.QuestCompletionPersistenceOverride = (attempt, candidate) =>
        {
            attempts++;
            observedUncommittedState = ReferenceEquals(owner.Quests.ActiveQuests[101], attempt) &&
                !owner.Quests.IsQuestComplete(101) && candidate.Body[37] && session.Packets.Count == 0;
            return committed;
        };

        quest.GoToNextStep();

        await Assert.That(observedUncommittedState).IsTrue();
        await Assert.That(owner.Quests.ActiveQuests[101]).IsSameReferenceAs(quest);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(quest.WriteData()).IsEquivalentTo(before);
        await Assert.That(owner.Quests.IsQuestComplete(101)).IsFalse();
        await Assert.That(owner.Achievements.GetAmount(1000)).IsEqualTo(0u);
        await Assert.That(owner.Achievements.GetAmount(1001)).IsEqualTo(0u);
        await Assert.That(owner.Achievements.IsCompleted(1000)).IsFalse();
        await Assert.That(session.Packets).IsEmpty();

        committed = true;
        quest.GoToNextStep();
        quest.GoToNextStep();

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(owner.Quests.IsQuestComplete(101)).IsTrue();
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Completed);
        await Assert.That(owner.Achievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(owner.Achievements.GetAmount(1001)).IsEqualTo(1u);
        await Assert.That(owner.Achievements.IsCompleted(1000)).IsTrue();
        await Assert.That(session.Packets.Count(packet => Opcode(packet) == SCOffsets.SCQuestContextCompletedPacket)).IsEqualTo(1);
        await Assert.That(session.Packets.Select(Opcode).SequenceEqual(new ushort[]
        {
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementCompletedPacket,
            SCOffsets.SCQuestContextCompletedPacket
        })).IsTrue();
    }

    [Test]
    public async Task DoReportEvents_KnownAbort_RetriesSavedSelectionWithoutRewardReplay()
    {
        using var data = CreateAchievementData();
        var owner = CreateOwner(data);
        var quest = CreateQuest(owner, 987);
        quest.SelectedRewardIndex = 7;
        var manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        var commits = 0;
        owner.Quests.QuestCompletionPersistenceOverride = (_, _) => { commits++; return commits > 1; };

        quest.GoToNextStep();
        manager.DoReportEvents(owner, quest.TemplateId, 0, 0, 8);

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(7);
        await Assert.That(owner.Quests.ActiveQuests[101]).IsSameReferenceAs(quest);

        manager.DoReportEvents(owner, quest.TemplateId, 0, 0, 7);
        manager.DoReportEvents(owner, quest.TemplateId, 0, 0, 7);

        await Assert.That(commits).IsEqualTo(2);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(owner.Achievements.GetAmount(1002)).IsEqualTo(1u);
        await Assert.That(owner.Achievements.GetAmount(1001)).IsEqualTo(1u);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TryCompleteQuest_RestoredRewardAbort_PreservesAuthoredReportRetry(int reportKind)
    {
        using var data = CreateAchievementData();
        var owner = CreateOwner(data);
        var original = CreateQuest(owner, 987, reportKind: reportKind);
        original.SelectedRewardIndex = 7;
        var saved = original.WriteData();
        owner.Quests.ActiveQuests.Clear();
        var restored = CreateQuest(owner, 987, reportKind: reportKind);
        restored.ReadData(saved);
        restored.RestoreLoadedState();
        var commits = 0;
        owner.Quests.QuestCompletionPersistenceOverride = (_, _) => { commits++; return commits > 1; };

        restored.GoToNextStep();

        var acceptsReport = reportKind switch
        {
            1 => restored.CanRetryCompletionReport(7, npcTemplateId: 42),
            2 => restored.CanRetryCompletionReport(7, doodadTemplateId: 43),
            _ => restored.CanRetryCompletionReport(7)
        };
        await Assert.That(acceptsReport).IsTrue();
        await Assert.That(restored.CanRetryCompletionReport(8, npcTemplateId: 42)).IsFalse();
        await Assert.That(restored.CanRetryCompletionReport(7, npcTemplateId: 99)).IsFalse();
        await Assert.That(restored.CanRetryCompletionReport(7, doodadTemplateId: 99)).IsFalse();
        if (reportKind is 1 or 2)
            await Assert.That(restored.CanRetryCompletionReport(7)).IsFalse();

        if (reportKind == 3)
        {
            var manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
            manager.DoReportEvents(owner, restored.TemplateId, 0, 0, 7);
        }
        else
            await Assert.That(owner.Quests.TryCompleteQuest(restored)).IsTrue();

        await Assert.That(commits).IsEqualTo(2);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(owner.Achievements.GetAmount(1002)).IsEqualTo(1u);
        await Assert.That(restored.SelectedRewardIndex).IsEqualTo(7);
    }

    [Test]
    public async Task TryCompleteQuest_RepeatableAttempt_CountsTypeForEachAttemptAndCategoryOnce()
    {
        using var data = CreateAchievementData();
        var owner = CreateOwner(data);
        var first = CreateQuest(owner, 987);
        var attempts = new List<long>();
        owner.Quests.QuestCompletionPersistenceOverride = (quest, _) =>
        {
            attempts.Add(quest.Id);
            return true;
        };

        first.GoToNextStep();
        var repeated = CreateQuest(owner, 988);
        first.GoToNextStep();
        repeated.GoToNextStep();
        repeated.GoToNextStep();

        await Assert.That(attempts).IsEquivalentTo(new long[] { 987, 988 });
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(owner.Achievements.GetAmount(1002)).IsEqualTo(2u);
        await Assert.That(owner.Achievements.GetAmount(1001)).IsEqualTo(1u);
    }

    [Test]
    public async Task TryCompleteQuest_ConcurrentReplay_CommitsAndPublishesOnce()
    {
        using var data = CreateAchievementData();
        var session = new RecordingSession();
        var owner = CreateOwner(data, session);
        var quest = CreateQuest(owner, 987);
        var commits = 0;
        owner.Quests.QuestCompletionPersistenceOverride = (_, _) =>
        {
            commits++;
            return true;
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => owner.Quests.TryCompleteQuest(quest))));

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(results.Count(result => result)).IsEqualTo(1);
        await Assert.That(owner.Achievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(session.Packets.Count(packet => Opcode(packet) == SCOffsets.SCQuestContextCompletedPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task TryCompleteQuest_BeforePacketsFails_KeepsCommittedStateAndRejectsReplay()
    {
        using var data = CreateAchievementData();
        var session = new RecordingSession();
        var owner = CreateOwner(data, session);
        var quest = CreateQuest(owner, 987);
        var commits = 0;
        owner.Quests.QuestCompletionPersistenceOverride = (_, _) => { commits++; return true; };
        owner.Quests.CompletionPersistenceFailpoint = stage =>
        {
            if (stage == QuestCompletionPersistenceStage.BeforePackets)
                throw new IOException("Injected failure before packets");
        };

        await Assert.That(() => quest.GoToNextStep()).Throws<IOException>();
        quest.GoToNextStep();

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(owner.Quests.IsQuestComplete(101)).IsTrue();
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Completed);
        await Assert.That(owner.Achievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(owner.Achievements.IsCompleted(1000)).IsTrue();
        await Assert.That(session.Packets).IsEmpty();
    }

    [Test]
    public async Task TryCompleteQuest_CleanupFails_DetachesAttemptBeforeCallbacks()
    {
        using var data = CreateAchievementData();
        var owner = CreateOwner(data);
        var quest = CreateQuest(owner, 987, failCleanup: true);
        var commits = 0;
        owner.Quests.QuestCompletionPersistenceOverride = (_, _) => { commits++; return true; };

        await Assert.That(() => quest.GoToNextStep()).Throws<IOException>();
        quest.GoToNextStep();

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(owner.Quests.IsQuestComplete(101)).IsTrue();
        await Assert.That(owner.Achievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(owner.Achievements.IsCompleted(1000)).IsTrue();
    }

    private static CharacterAchievementsTests.AchievementDataBuilder CreateAchievementData()
    {
        var data = new CharacterAchievementsTests.AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.CompleteQuestType, 101);
        data.AddRecord(101, CharRecordKind.CompleteQuestCategory, 35);
        data.AddAchievement(1000, 1, false);
        data.AddAchievement(1001, 100, false);
        data.AddAchievement(1002, 100, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1001, 101);
        data.AddObjective(3, 1002, 100);
        return data;
    }

    private static CharacterMock CreateOwner(CharacterAchievementsTests.AchievementDataBuilder data, RecordingSession session = null)
    {
        var owner = new CharacterMock { Id = 7, Name = "Questor" };
        owner.Quests = new CharacterQuests(owner);
        var achievements = new CharacterAchievements(owner, data.Build(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 16, 0, 0, TimeSpan.Zero)),
            () => throw new InvalidOperationException("Deferred achievements reached independent persistence."));
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, achievements);
        if (session != null)
            owner.Connection = new GameConnection(session) { ActiveChar = owner };
        return owner;
    }

    private static Quest CreateQuest(CharacterMock owner, long attemptId, bool failCleanup = false, int reportKind = 0)
    {
        var template = new QuestTemplate { Id = 101, CategoryId = 35, Repeatable = true };
        var reward = new QuestComponentTemplate(template) { Id = 1011, KindId = QuestComponentKind.Reward };
        if (reportKind == 0)
            reward.ActTemplates.Add(new QuestActConAutoComplete(reward) { ActId = 1013 });
        else
        {
            var ready = new QuestComponentTemplate(template) { Id = 1014, KindId = QuestComponentKind.Ready };
            ready.ActTemplates.Add(reportKind switch
            {
                1 => new QuestActConReportNpc(ready) { ActId = 1015, NpcId = 42 },
                2 => new QuestActConReportDoodad(ready) { ActId = 1015, DoodadId = 43 },
                _ => new QuestActConReportJournal(ready) { ActId = 1015 }
            });
            template.Components.Add(ready.Id, ready);
        }
        if (failCleanup)
            reward.ActTemplates.Add(new FailingCleanupAct(reward) { ActId = 1012 });
        template.Components.Add(reward.Id, reward);
        var quest = new Quest(template, owner, Mock.Of<IQuestManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Id = attemptId,
            Step = QuestComponentKind.Reward,
            Status = QuestStatus.Ready
        };
        owner.Quests.ActiveQuests.Add(template.Id, quest);
        return quest;
    }

    private static ushort Opcode(byte[] packet)
    {
        return BitConverter.ToUInt16(packet, 6);
    }

    private sealed class FailingCleanupAct(QuestComponentTemplate parent) : QuestActTemplate(parent)
    {
        public override void QuestCleanup(Quest quest)
        {
            throw new IOException("Injected quest cleanup failure");
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet)
        {
            Packets.Add(packet.ToArray());
        }

        public void AddAttribute(string name, object attribute)
        {
            _attributes.Add(name, attribute);
        }

        public object GetAttribute(string name)
        {
            return _attributes.GetValueOrDefault(name);
        }

        public void ClearAttribute(string name)
        {
            _attributes.Remove(name);
        }

        public void Close()
        {
        }
    }
}
