using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

public sealed class QuestActCheckSphereTests
{
    [Test]
    [Arguments(100u, 200u, 1)]
    [Arguments(101u, 200u, 0)]
    [Arguments(100u, 201u, 0)]
    public async Task OnEnterSphere_OnlyMatchingQuestAndComponentChangesCondition(uint questId, uint componentId, int expected)
    {
        var (quest, act, template) = CreateAct();
        template.OnEnterSphere(act, null, new OnEnterSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = questId, ComponentId = componentId }
        });

        await Assert.That(act.RunAct()).IsEqualTo(expected != 0);
        await Assert.That(quest.Objectives.All(value => value == 0)).IsTrue();
    }

    [Test]
    [Arguments(100u, 200u, 0)]
    [Arguments(101u, 200u, 1)]
    [Arguments(100u, 201u, 1)]
    public async Task OnExitSphere_OnlyMatchingQuestAndComponentChangesCondition(uint questId, uint componentId, int expected)
    {
        var (quest, act, template) = CreateAct();
        act.OverrideObjectiveCompleted = true;
        template.OnExitSphere(act, null, new OnExitSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = questId, ComponentId = componentId }
        });

        await Assert.That(act.RunAct()).IsEqualTo(expected != 0);
        await Assert.That(quest.Objectives.All(value => value == 0)).IsTrue();
    }

    [Test]
    public async Task OnEnterSphere_DifferentAct_DoesNotChangeCondition()
    {
        var (quest, act, template) = CreateAct();
        act.Template = new QuestActCheckSphere(template.ParentComponent) { ActId = 999 };
        template.OnEnterSphere(act, null, new OnEnterSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = 100, ComponentId = 200 }
        });

        await Assert.That(act.OverrideObjectiveCompleted).IsFalse();
    }

    [Test]
    public async Task FinalizeAction_RuntimeIdDiffersFromTemplateId_RemovesRegisteredTriggers()
    {
        var (quest, act, template) = CreateAct();
        var manager = ((GameObject)quest.Owner).ParentWorld.SphereQuestManager;
        manager.AddSphereQuestTrigger(new SphereQuestTrigger
        {
            Quest = quest, Owner = quest.Owner, Sphere = new SphereQuest { QuestId = quest.TemplateId, ComponentId = 200 }
        });
        template.FinalizeAction(quest, act);

        await Assert.That(manager.GetSphereQuestTriggers().Count).IsEqualTo(0);
    }

    [Test]
    public async Task InitializeAndFinalizeAction_RepeatedQuestCycles_RestoresTriggersAndSubscriptions()
    {
        var (quest, act, template) = CreateAct();
        var manager = ((GameObject)quest.Owner).ParentWorld.SphereQuestManager;
        var sphere = new SphereQuest { QuestId = 100, ComponentId = 200 };
        typeof(SphereQuestManager).GetField("_sphereQuests", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<uint, List<SphereQuest>> { [200] = [sphere] });
        var initialEnterSubscriptions = quest.Owner.Events.OnEnterSphere.GetInvocationList().Length;
        var initialExitSubscriptions = quest.Owner.Events.OnExitSphere.GetInvocationList().Length;
        for (var cycle = 0; cycle < 20; cycle++)
        {
            template.InitializeAction(quest, act);
            await Assert.That(act.RunAct()).IsFalse();
            await Assert.That(manager.GetSphereQuestTriggers().Count).IsEqualTo(1);
            quest.Owner.Events.OnEnterSphere(quest.Owner, new OnEnterSphereArgs { SphereQuest = sphere });
            await Assert.That(act.RunAct()).IsTrue();
            template.FinalizeAction(quest, act);
            await Assert.That(manager.GetSphereQuestTriggers().Count).IsEqualTo(0);
            await Assert.That(quest.Owner.Events.OnEnterSphere.GetInvocationList().Length).IsEqualTo(initialEnterSubscriptions);
            await Assert.That(quest.Owner.Events.OnExitSphere.GetInvocationList().Length).IsEqualTo(initialExitSubscriptions);
        }
    }

    private static (Quest Quest, QuestAct Act, QuestActCheckSphere Template) CreateAct()
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "test" }, 0, true, 1);
        world.SphereQuestManager = new SphereQuestManager(world);
        var owner = SphereQuestManagerTests.CreateCharacter(world, 7);
        var quest = SphereQuestManagerTests.CreateQuest(owner);
        quest.TemplateId = 100;
        var component = new QuestComponentTemplate(new QuestTemplate { Id = 100 }) { Id = 200 };
        var template = new QuestActCheckSphere(component) { ActId = 300, Count = 1 };
        var questComponent = new QuestComponent(new QuestStep(QuestComponentKind.Progress, quest), component);
        return (quest, new QuestAct(questComponent, template), template);
    }
}
