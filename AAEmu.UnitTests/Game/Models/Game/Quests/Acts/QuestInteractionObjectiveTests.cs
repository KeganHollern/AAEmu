using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

public sealed class QuestInteractionObjectiveTests
{
    [Test]
    [Arguments(2590u, 19u, 5522u, 1)]
    [Arguments(2591u, 19u, 5522u, 0)]
    [Arguments(2590u, 20u, 5522u, 0)]
    [Arguments(2590u, 19u, 5521u, 0)]
    [Arguments(2590u, 0u, 5522u, 0)]
    [Arguments(2590u, 19u, 0u, 0)]
    public async Task Interaction_RequiresTheAuthoredDoodadFunctionAndResultingPhase(
        uint doodadId, uint interactionId, uint phase, int expected)
    {
        var owner = new CharacterMock { Id = 7 };
        var (quest, manager) = CreateQuest(owner, 2590, 19, 5522);
        manager.DoDoodadInteractionEvents(owner, owner, doodadId, (WorldInteractionType)interactionId, phase);
        await Assert.That(quest.Objectives[0]).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0u, 19u, 5522u)]
    [Arguments(2590u, 0u, 5522u)]
    [Arguments(2590u, 19u, 0u)]
    public async Task Interaction_ZeroConstraintAllowsAnyValueOnlyForThatField(uint doodadId, uint interactionId, uint phase)
    {
        var owner = new CharacterMock { Id = 7 };
        var (quest, manager) = CreateQuest(owner, doodadId, interactionId, phase);
        manager.DoDoodadInteractionEvents(owner, owner, doodadId == 0 ? 5000 : doodadId,
            (WorldInteractionType)(interactionId == 0 ? 50 : interactionId), phase == 0 ? 9000 : phase);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }

    [Test]
    public async Task Interaction_UnsharedOtherPlayerAndFinalizedActDoNotReceiveCredit()
    {
        var owner = new CharacterMock { Id = 7 };
        var other = new CharacterMock { Id = 8 };
        var (quest, manager) = CreateQuest(owner, 2590, 19, 5522);
        manager.DoDoodadInteractionEvents(other, owner, 2590, (WorldInteractionType)19, 5522);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        quest.CurrentStep.FinalizeStep();
        manager.DoDoodadInteractionEvents(owner, owner, 2590, (WorldInteractionType)19, 5522);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
    }

    private static (Quest Quest, QuestManager Manager) CreateQuest(CharacterMock owner, uint doodadId, uint interactionId, uint phase)
    {
        var template = new QuestTemplate { Id = 100 };
        var component = new QuestComponentTemplate(template) { Id = 1001, KindId = QuestComponentKind.Progress };
        component.ActTemplates.Add(new QuestActObjInteraction(component)
        {
            ActId = 1002, DetailId = 163, ThisComponentObjectiveIndex = 0, Count = 3,
            DoodadId = doodadId, WorldInteractionId = (WorldInteractionType)interactionId, Phase = phase
        });
        template.Components.Add(component.Id, component);
        var manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        var quest = new Quest(template, owner, manager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object);
        quest.Step = QuestComponentKind.Progress;
        return (quest, manager);
    }
}
