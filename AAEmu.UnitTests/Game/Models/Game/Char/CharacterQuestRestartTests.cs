using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class CharacterQuestRestartTests
{
    [Test]
    public async Task RestartMainQuest_FailedAttempt_CommitsFreshStateBeforeActivation()
    {
        var owner = CreateOwner();
        var failed = CreateQuest(owner);
        failed.Objectives = [1, 2, 3, 4, 5];
        failed.SelectedRewardIndex = 3;
        failed.QuestRewardCoinsPool = 500;
        failed.QuestRewardExpPool = 600;
        failed.QuestRewardItemsPool.Add(new ItemCreationDefinition(50001, 1));
        failed.AppliedSideEffectActIds.Add(901);
        failed.AppliedComponentEffectIds.Add(902);
        failed.Time = DateTime.UtcNow.AddMinutes(-1);
        owner.Quests.SetCompletedQuestFlag(102, true, _ => true, out _, out _);
        Quest persisted = null;
        byte[] savedData = null;
        var activeDuringCommit = false;
        var result = owner.Quests.RestartMainQuest(101, restarted =>
        {
            persisted = restarted;
            savedData = restarted.WriteData();
            activeDuringCommit = ReferenceEquals(owner.Quests.ActiveQuests[101], failed);
            return true;
        });

        var active = owner.Quests.ActiveQuests[101];
        await Assert.That(result).IsTrue();
        await Assert.That(activeDuringCommit).IsTrue();
        await Assert.That(ReferenceEquals(active, persisted)).IsTrue();
        await Assert.That(ReferenceEquals(active, failed)).IsFalse();
        await Assert.That(active.Id).IsEqualTo(987L);
        await Assert.That(active.TemplateId).IsEqualTo(101u);
        await Assert.That(active.Step).IsEqualTo(QuestComponentKind.Start);
        await Assert.That(active.Status).IsEqualTo(QuestStatus.Progress);
        await Assert.That(active.QuestAcceptorType).IsEqualTo(QuestAcceptorType.Npc);
        await Assert.That(active.AcceptorId).IsEqualTo(42u);
        await Assert.That(active.Objectives).IsEquivalentTo(new int[5]);
        await Assert.That(active.SelectedRewardIndex).IsEqualTo(0);
        await Assert.That(active.QuestRewardCoinsPool).IsEqualTo(0);
        await Assert.That(active.QuestRewardExpPool).IsEqualTo(0);
        await Assert.That(active.QuestRewardItemsPool).IsEmpty();
        await Assert.That(active.AppliedSideEffectActIds).IsEmpty();
        await Assert.That(active.AppliedComponentEffectIds).IsEmpty();
        await Assert.That(active.Time).IsEqualTo(default(DateTime));
        await Assert.That(owner.Quests.IsQuestComplete(101)).IsFalse();
        await Assert.That(owner.Quests.IsQuestComplete(102)).IsTrue();

        var reloadedOwner = CreateOwner();
        var reloaded = CreateQuest(reloadedOwner);
        reloadedOwner.Quests.ActiveQuests.Clear();
        reloaded.Status = persisted.Status;
        reloaded.ReadData(savedData!);
        reloadedOwner.Quests.AddLoadedQuest(reloaded);
        await Assert.That(reloaded.WriteData()).IsEquivalentTo(savedData);
        await Assert.That(reloaded.Step).IsEqualTo(QuestComponentKind.Start);
        await Assert.That(reloadedOwner.Quests.RestartMainQuest(101, _ => throw new InvalidOperationException("Replay reached persistence"))).IsFalse();
    }

    [Test]
    public async Task RestartMainQuest_AbortedCommit_KeepsFailedStateForRetry()
    {
        var owner = CreateOwner();
        var failed = CreateQuest(owner);
        failed.Objectives[0] = 7;
        var before = failed.WriteData();

        await Assert.That(owner.Quests.RestartMainQuest(101, _ => false)).IsFalse();
        await Assert.That(ReferenceEquals(owner.Quests.ActiveQuests[101], failed)).IsTrue();
        await Assert.That(failed.WriteData()).IsEquivalentTo(before);
        await Assert.That(owner.Quests.RestartMainQuest(101, _ => true)).IsTrue();
        await Assert.That(owner.Quests.RestartMainQuest(101, _ => throw new InvalidOperationException("Replay reached persistence"))).IsFalse();
    }

    [Test]
    [Arguments("unknown")]
    [Arguments("foreign-owner")]
    [Arguments("progress")]
    [Arguments("ready")]
    [Arguments("completed")]
    [Arguments("not-restartable")]
    [Arguments("not-main")]
    [Arguments("missing-start")]
    public async Task RestartMainQuest_InvalidState_RejectsWithoutPersistence(string scenario)
    {
        var owner = CreateOwner();
        var failed = CreateQuest(owner);
        switch (scenario)
        {
            case "foreign-owner": failed.Owner = CreateOwner(); break;
            case "progress": failed.Step = QuestComponentKind.Progress; break;
            case "ready": failed.Status = QuestStatus.Ready; break;
            case "completed": owner.Quests.SetCompletedQuestFlag(101, true, _ => true, out _, out _); break;
            case "not-restartable": failed.Template.RestartOnFail = false; break;
            case "not-main": failed.Template.DetailId = QuestDetail.Normal; break;
            case "missing-start": failed.QuestSteps.Clear(); break;
        }

        await Assert.That(owner.Quests.RestartMainQuest(scenario == "unknown" ? uint.MaxValue : 101,
            _ => throw new InvalidOperationException("Invalid restart reached persistence"))).IsFalse();
        await Assert.That(ReferenceEquals(owner.Quests.ActiveQuests[101], failed)).IsTrue();
    }

    [Test]
    public async Task RestartMainQuest_ConcurrentReplay_CommitsOnce()
    {
        var owner = CreateOwner();
        CreateQuest(owner);
        var commits = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            owner.Quests.RestartMainQuest(101, _ => { Interlocked.Increment(ref commits); return true; }))));

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(results.Count(result => result)).IsEqualTo(1);
    }

    [Test]
    public async Task RestartMainQuest_StartedPacketFails_StillActivatesCommittedAttempt()
    {
        var owner = CreateOwner();
        var manager = Mock.Of<IQuestManager>();
        CreateQuest(owner, manager.Object);
        var session = Mock.Of<ISession>();
        session.SendPacket(Any<byte[]>()).Throws(new IOException("Injected packet loss"));
        owner.Connection = new GameConnection(session.Object) { ActiveChar = owner };

        await Assert.That(() => owner.Quests.RestartMainQuest(101, _ => true)).Throws<IOException>();

        var active = owner.Quests.ActiveQuests[101];
        await Assert.That(active.Step).IsEqualTo(QuestComponentKind.Start);
        manager.EnqueueEvaluation(active).WasCalled(Times.Once);
        await Assert.That(owner.Quests.RestartMainQuest(101, _ => false)).IsFalse();
    }

    [Test]
    public async Task RestoreLoadedState_LegacyFailedStep_NormalizesFailedStatus()
    {
        var owner = CreateOwner();
        var failed = CreateQuest(owner);
        failed.Status = QuestStatus.Progress;
        failed.RestoreLoadedState();

        await Assert.That(failed.Status).IsEqualTo(QuestStatus.Failed);
        await Assert.That(owner.Quests.RestartMainQuest(101, _ => true)).IsTrue();
    }

    [Test]
    public async Task RestartPacket_ExactBody_ReadsContextIdThroughFinalByte()
    {
        var packet = new CSRestartMainQuestPacket();
        var body = new PacketStream().Write(0xf1234567u);
        packet.Read(body);

        await Assert.That(packet.TypeId).IsEqualTo(CSOffsets.CSRestartMainQuestPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(packet.QuestContextId).IsEqualTo(0xf1234567u);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    public async Task RestartPacket_IncompleteOrTrailingBody_RejectsBeforeAction(int length)
    {
        var packet = new CSRestartMainQuestPacket();
        await Assert.That(() => packet.Read(new PacketStream(new byte[length]))).Throws<InvalidDataException>();
        await Assert.That(packet.QuestContextId).IsEqualTo(0u);
    }

    private static CharacterMock CreateOwner()
    {
        var owner = new CharacterMock { Id = 7, Name = "Questor" };
        owner.Quests = new CharacterQuests(owner);
        return owner;
    }

    private static Quest CreateQuest(CharacterMock owner, IQuestManager questManager = null)
    {
        var template = new QuestTemplate { Id = 101, DetailId = QuestDetail.Main, RestartOnFail = true };
        var start = new QuestComponentTemplate(template) { Id = 1011, KindId = QuestComponentKind.Start };
        start.ActTemplates.Add(new QuestActConAcceptNpc(start) { ActId = 1012, NpcId = 42 });
        template.Components.Add(start.Id, start);
        var quest = new Quest(template, owner, questManager ?? Mock.Of<IQuestManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Id = 987,
            Step = QuestComponentKind.Fail,
            Status = QuestStatus.Failed,
            QuestAcceptorType = QuestAcceptorType.Npc,
            AcceptorId = 42
        };
        owner.Quests.ActiveQuests.Add(template.Id, quest);
        return quest;
    }
}
