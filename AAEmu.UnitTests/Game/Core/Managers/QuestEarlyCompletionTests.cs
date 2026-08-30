using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class QuestEarlyCompletionTests
{
    private const uint QuestId = 303;
    private const uint OtherQuestId = 9_303;
    private const uint ReportNpcTemplateId = 8_141;
    private const uint ObjectiveNpcTemplateId = 3_452;
    private const uint ObjectiveNpcObjectId = 44_398;
    private const uint ReportNpcObjectId = 81_410;
    private const uint StaleObjectId = 99_999;
    private const int RequiredObjectiveCount = 12;

    private static readonly FieldInfo ParentWorldField = typeof(GameObject)
        .GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public async Task AddQuestFromNpc_MissingObject_ReturnsFalseWithoutTargetMutation()
    {
        var world = CreateWorld();
        var owner = CreateOwner(world);
        var originalTarget = new Npc { ObjId = 77, TemplateId = 100 };
        owner.CurrentTarget = originalTarget;

        var result = owner.Quests.AddQuestFromNpc(QuestId, StaleObjectId);

        await Assert.That(result).IsFalse();
        await Assert.That(owner.CurrentTarget).IsSameReferenceAs(originalTarget);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
    }

    [Test]
    public async Task AddQuestFromDoodad_MissingObject_ReturnsFalseWithoutTargetMutation()
    {
        var world = CreateWorld();
        var owner = CreateOwner(world);
        var originalTarget = new Npc { ObjId = 77, TemplateId = 100 };
        owner.CurrentTarget = originalTarget;

        var result = owner.Quests.AddQuestFromDoodad(QuestId, StaleObjectId);

        await Assert.That(result).IsFalse();
        await Assert.That(owner.CurrentTarget).IsSameReferenceAs(originalTarget);
        await Assert.That(owner.Quests.ActiveQuests).IsEmpty();
    }

    [Test]
    public async Task IsValidQuestAcceptor_NpcSource_AcceptsOnlyConfiguredTemplate()
    {
        var template = new QuestTemplate { Id = 304 };
        var startComponent = new QuestComponentTemplate(template)
        {
            Id = 3_041,
            KindId = QuestComponentKind.Start
        };
        startComponent.ActTemplates.Add(new QuestActConAcceptNpc(startComponent)
        {
            ActId = 3_042,
            NpcId = ReportNpcTemplateId
        });
        template.Components.Add(startComponent.Id, startComponent);

        var valid = CharacterQuests.IsValidQuestAcceptor(
            template,
            QuestAcceptorType.Npc,
            ReportNpcTemplateId);
        var invalid = CharacterQuests.IsValidQuestAcceptor(
            template,
            QuestAcceptorType.Npc,
            ObjectiveNpcTemplateId);

        await Assert.That(valid).IsTrue();
        await Assert.That(invalid).IsFalse();
    }

    [Test]
    public async Task IsValidQuestAcceptor_DoodadSource_AcceptsOnlyConfiguredTemplate()
    {
        const uint expectedDoodadTemplateId = 12_345;
        var template = new QuestTemplate { Id = 305 };
        var startComponent = new QuestComponentTemplate(template)
        {
            Id = 3_051,
            KindId = QuestComponentKind.Start
        };
        startComponent.ActTemplates.Add(new QuestActConAcceptDoodad(startComponent)
        {
            ActId = 3_052,
            DoodadId = expectedDoodadTemplateId
        });
        template.Components.Add(startComponent.Id, startComponent);

        var valid = CharacterQuests.IsValidQuestAcceptor(
            template,
            QuestAcceptorType.Doodad,
            expectedDoodadTemplateId);
        var invalid = CharacterQuests.IsValidQuestAcceptor(
            template,
            QuestAcceptorType.Doodad,
            expectedDoodadTemplateId + 1);

        await Assert.That(valid).IsTrue();
        await Assert.That(invalid).IsFalse();
    }

    [Test]
    public async Task TryCompleteQuestAsLetItDone_HalfProgressFromObjectiveNpc_AdvancesToReward()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        AddNpc(world, ObjectiveNpcObjectId, ObjectiveNpcTemplateId);
        var quest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount / 2);

        var result = manager.TryCompleteQuestAsLetItDone(owner, QuestId, ObjectiveNpcObjectId, 2);

        await Assert.That(result).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(2);
    }

    [Test]
    public async Task DoReportEvents_FullProgressAtReportNpc_AdvancesThroughReadyToReward()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        AddNpc(world, ReportNpcObjectId, ReportNpcTemplateId);
        var quest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount);

        manager.DoReportEvents(owner, QuestId, ReportNpcObjectId, 0, 3);

        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(3);

        var stepCompleted = quest.RunCurrentStep();

        await Assert.That(stepCompleted).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
    }

    [Test]
    public async Task TryCompleteQuestAsLetItDone_StaleObjectiveObject_AdvancesToReward()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        var quest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount / 2);
        quest.SelectedRewardIndex = -1;

        var result = manager.TryCompleteQuestAsLetItDone(owner, QuestId, StaleObjectId, 2);

        await Assert.That(result).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(2);
    }

    [Test]
    public async Task TryCompleteQuestAsLetItDone_ZeroObjectiveObject_AdvancesToReward()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        var quest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount / 2);
        quest.SelectedRewardIndex = -1;

        var result = manager.TryCompleteQuestAsLetItDone(owner, QuestId, 0, 2);

        await Assert.That(result).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(2);
    }

    [Test]
    public async Task TryCompleteQuestAsLetItDone_BelowHalfProgress_DoesNotChangeQuest()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        var quest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount / 2 - 1);
        quest.SelectedRewardIndex = -1;

        var result = manager.TryCompleteQuestAsLetItDone(owner, QuestId, 0, 2);

        await Assert.That(result).IsFalse();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task TryCompleteQuestAsLetItDone_SharedReportNpc_ChangesOnlyRequestedQuest()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        AddNpc(world, ObjectiveNpcObjectId, ObjectiveNpcTemplateId);
        var requestedQuest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount / 2);
        var otherQuest = AddActiveQuest(owner, manager, OtherQuestId, RequiredObjectiveCount);

        var result = manager.TryCompleteQuestAsLetItDone(owner, QuestId, ObjectiveNpcObjectId, 2);

        await Assert.That(result).IsTrue();
        await Assert.That(requestedQuest.Step).IsEqualTo(QuestComponentKind.Reward);
        await Assert.That(otherQuest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(otherQuest.SelectedRewardIndex).IsEqualTo(0);
    }

    [Test]
    public async Task DoReportEvents_SharedReportNpc_ChangesOnlyRequestedQuest()
    {
        var manager = CreateManager();
        var world = CreateWorld();
        var owner = CreateOwner(world);
        AddNpc(world, ReportNpcObjectId, ReportNpcTemplateId);
        var requestedQuest = AddActiveQuest(owner, manager, QuestId, RequiredObjectiveCount);
        var otherQuest = AddActiveQuest(owner, manager, OtherQuestId, RequiredObjectiveCount);

        manager.DoReportEvents(owner, QuestId, ReportNpcObjectId, 0, 3);

        await Assert.That(requestedQuest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(requestedQuest.SelectedRewardIndex).IsEqualTo(3);
        await Assert.That(otherQuest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(otherQuest.SelectedRewardIndex).IsEqualTo(0);
    }

    private static QuestManager CreateManager()
    {
        return new QuestManager(
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IZoneManager>().Object);
    }

    private static WorldInstance CreateWorld()
    {
        return new WorldInstance(
            new WorldTemplate { Id = 1, Name = "quest-test" },
            0,
            true,
            1);
    }

    private static CharacterMock CreateOwner(WorldInstance world)
    {
        var owner = new CharacterMock
        {
            Id = 7,
            ObjId = 70,
            Name = "Questor"
        };
        SetParentWorld(owner, world);
        owner.Quests = new CharacterQuests(owner);
        return owner;
    }

    private static Npc AddNpc(WorldInstance world, uint objectId, uint templateId)
    {
        var npc = new Npc
        {
            ObjId = objectId,
            TemplateId = templateId,
            Template = new NpcTemplate { Id = templateId, Name = $"NPC {templateId}", Scale = 1f }
        };
        SetParentWorld(npc, world);
        world.AddObject(npc);
        return npc;
    }

    private static Quest AddActiveQuest(
        Character owner,
        QuestManager manager,
        uint questId,
        int objectiveCount)
    {
        var template = new QuestTemplate
        {
            Id = questId,
            LetItDone = true
        };

        var progressComponent = new QuestComponentTemplate(template)
        {
            Id = questId * 10 + 1,
            KindId = QuestComponentKind.Progress
        };
        progressComponent.ActTemplates.Add(new QuestActObjMonsterHunt(progressComponent)
        {
            ActId = questId * 10 + 2,
            NpcId = 50_001,
            Count = RequiredObjectiveCount,
            ThisComponentObjectiveIndex = 0
        });

        var readyComponent = new QuestComponentTemplate(template)
        {
            Id = questId * 10 + 3,
            KindId = QuestComponentKind.Ready
        };
        readyComponent.ActTemplates.Add(new QuestActConReportNpc(readyComponent)
        {
            ActId = questId * 10 + 4,
            NpcId = ReportNpcTemplateId
        });

        template.Components.Add(progressComponent.Id, progressComponent);
        template.Components.Add(readyComponent.Id, readyComponent);

        var quest = new Quest(
            template,
            owner,
            manager,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object)
        {
            Step = QuestComponentKind.Progress
        };
        quest.Objectives[0] = objectiveCount;
        owner.Quests.ActiveQuests.Add(questId, quest);
        return quest;
    }

    private static void SetParentWorld(GameObject gameObject, WorldInstance world)
    {
        ParentWorldField.SetValue(gameObject, world);
    }
}
