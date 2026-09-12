using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Spheres;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.World;

[NotInParallel]
public sealed class SphereQuestTriggerTests
{
    private readonly Dictionary<FieldInfo, object> _previousSingletons = [];
    private CharacterMock _owner;
    private WorldInstance _world;
    private WorldInstance _otherWorld;
    private int _entered;
    private int _exited;

    [Before(Test)]
    public void SetUp()
    {
        SetSingleton(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        SetSingleton(new UnitRequirementsGameData());
        var spheres = new SphereGameData();
        SetField(spheres, "_spheres", new Dictionary<uint, Spheres>());
        SetField(spheres, "_sphereQuests", new Dictionary<uint, SphereQuests>());
        SetSingleton(spheres);

        var worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetSingleton(worldManager);
        _world = new WorldInstance(new WorldTemplate { Id = 1, Name = "first" }, 0, true, 1);
        _otherWorld = new WorldInstance(new WorldTemplate { Id = 2, Name = "second" }, 0, true, 2);
        _world.SphereQuestManager = new SphereQuestManager(_world);
        _otherWorld.SphereQuestManager = new SphereQuestManager(_otherWorld);
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            new Dictionary<uint, WorldInstance> { [1] = _world, [2] = _otherWorld }));

        _owner = new CharacterMock { Id = 7, ObjId = 70, ParentWorld = _world };
        _owner.Quests = new CharacterQuests(_owner);
        _owner.Region = QuestInteractionTestModels.CreateRegion(_world);
        _entered = 0;
        _exited = 0;
        _owner.Events.OnEnterSphere += (_, _) => _entered++;
        _owner.Events.OnExitSphere += (_, _) => _exited++;
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousSingletons)
            field.SetValue(null, previous);
        _previousSingletons.Clear();
    }

    [Test]
    [Arguments(9.999f, true)]
    [Arguments(10f, true)]
    [Arguments(10.001f, false)]
    public async Task Contains_VerticalBoundary_UsesInclusiveThreeDimensionalRadius(float height, bool expected)
    {
        var sphere = new SphereQuest { Xyz = new Vector3(100, 100, 100), Radius = 10 };
        await Assert.That(sphere.Contains(sphere.Xyz + new Vector3(0, 0, height))).IsEqualTo(expected);
    }

    [Test]
    public async Task Tick_FirstSampleInsideOriginSphere_EntersOnceThenExits()
    {
        var trigger = CreateTrigger();
        trigger.Tick(TimeSpan.Zero);
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(1);

        _owner.Transform.Local.Position = new Vector3(0, 0, 10.001f);
        trigger.Tick(TimeSpan.Zero);
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_exited).IsEqualTo(1);

        _owner.Transform.Local.Position = Vector3.Zero;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(2);
    }

    [Test]
    public async Task Tick_NpcMovesPastStationaryPlayer_UsesMatchingTemplateAndKeepsPreviousState()
    {
        var trigger = CreateTrigger();
        trigger.NpcTemplate = 100;
        var wrongNpc = new Npc { ObjId = 80, TemplateId = 200, ParentWorld = _world };
        var matchingNpc = new Npc { ObjId = 81, TemplateId = 100, ParentWorld = _world };
        matchingNpc.Transform.Local.Position = new Vector3(20, 0, 0);
        SetRegionObjects(wrongNpc, matchingNpc);

        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(0);
        matchingNpc.Transform.Local.Position = new Vector3(9, 0, 0);
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(1);
        matchingNpc.Transform.Local.Position = new Vector3(20, 0, 0);
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_exited).IsEqualTo(1);
    }

    [Test]
    public async Task Tick_NpcFromAnotherInstance_DoesNotEnter()
    {
        var trigger = CreateTrigger();
        trigger.NpcTemplate = 100;
        SetRegionObjects(new Npc { ObjId = 81, TemplateId = 100, ParentWorld = _otherWorld });
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_ActRequirementChangesWhileInside_RechecksWithoutPlayerMovement()
    {
        var trigger = CreateTrigger();
        trigger.SphereId = 500;
        SetField(SphereGameData.Instance, "_spheres", new Dictionary<uint, Spheres> { [500] = new() { Id = 500 } });
        var requirement = new UnitReqs { OwnerId = 500, KindType = UnitReqsKindType.Level, Value1 = 100 };
        typeof(UnitRequirementsGameData).GetProperty("_unitReqsByOwnerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(UnitRequirementsGameData.Instance, new Dictionary<string, List<UnitReqs>> { ["Sphere"] = [requirement] });

        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(0);
        requirement.Value1 = 0;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_entered).IsEqualTo(1);
        requirement.Value1 = 100;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(_exited).IsEqualTo(1);
    }

    [Test]
    public async Task ParentWorld_ChangesInstance_RebindsCurrentSphereActsWithoutRestartingQuest()
    {
        var template = new QuestTemplate { Id = 100 };
        var component = new QuestComponentTemplate(template) { Id = 101, KindId = QuestComponentKind.Progress };
        component.ActTemplates.Add(new QuestActObjSphere(component) { ActId = 1000, SphereId = 500, ThisComponentObjectiveIndex = 0 });
        component.ActTemplates.Add(new QuestActCheckSphere(component) { ActId = 1001, SphereId = 501 });
        template.Components[component.Id] = component;
        var quest = new Quest(template, _owner, Mock.Of<IQuestManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object, false);
        SetField(quest, "_step", QuestComponentKind.Progress);
        _owner.Quests.ActiveQuests[quest.TemplateId] = quest;
        var checkAct = quest.CurrentStep.Components[101].Acts.Single(act => act.Id == 1001);
        checkAct.OverrideObjectiveCompleted = true;
        var firstSphere = new SphereQuest { ComponentId = 101, QuestId = 100, WorldId = "first", Radius = 10 };
        var secondSphere = new SphereQuest { ComponentId = 101, QuestId = 100, WorldId = "second", Xyz = new Vector3(100), Radius = 10 };
        SetField(_world.SphereQuestManager, "_sphereQuests", new Dictionary<uint, List<SphereQuest>> { [101] = [firstSphere] });
        SetField(_otherWorld.SphereQuestManager, "_sphereQuests", new Dictionary<uint, List<SphereQuest>> { [101] = [secondSphere] });
        _world.SphereQuestManager.AddSphereQuestTriggers(_owner, quest, 101, 0, 500);

        _owner.ParentWorld = _otherWorld;

        await Assert.That(_world.SphereQuestManager.GetSphereQuestTriggers()).IsEmpty();
        var triggers = _otherWorld.SphereQuestManager.GetSphereQuestTriggers();
        await Assert.That(triggers.Count).IsEqualTo(2);
        await Assert.That(triggers[0].Sphere).IsSameReferenceAs(secondSphere);
        await Assert.That(triggers[0].SphereId).IsEqualTo(500u);
        await Assert.That(_owner.Quests.ActiveQuests[100]).IsSameReferenceAs(quest);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(checkAct.OverrideObjectiveCompleted).IsFalse();

        _owner.ParentWorld = _world;
        await Assert.That(_otherWorld.SphereQuestManager.GetSphereQuestTriggers()).IsEmpty();
        await Assert.That(_world.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ParentWorld_DepartsSource_ClearsLocationStateAndPreservesCompletedArrival(bool detachFromWorld)
    {
        SetField(SphereGameData.Instance, "_spheres", new Dictionary<uint, Spheres>
        {
            [500] = new() { Id = 500, TriggerConditionId = AreaSphereTriggerCondition.None },
            [501] = new() { Id = 501, TriggerConditionId = AreaSphereTriggerCondition.TriggerEveryNTimeAfter }
        });
        var template = new QuestTemplate { Id = 100 };
        var component = new QuestComponentTemplate(template) { Id = 101, KindId = QuestComponentKind.Progress };
        component.ActTemplates.Add(new QuestActObjSphere(component)
        {
            ActId = 1000, SphereId = 500, ThisComponentObjectiveIndex = 0
        });
        component.ActTemplates.Add(new QuestActObjSphere(component)
        {
            ActId = 1001, SphereId = 501, ThisComponentObjectiveIndex = 1
        });
        component.ActTemplates.Add(new QuestActObjItemUse(component)
        {
            ActId = 1002, ItemId = 5130, Count = 3, ThisComponentObjectiveIndex = 2
        });
        component.ActTemplates.Add(new QuestActCheckSphere(component) { ActId = 1003, SphereId = 502 });
        template.Components[component.Id] = component;
        var quest = new Quest(template, _owner, Mock.Of<IQuestManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object, false);
        SetField(quest, "_step", QuestComponentKind.Progress);
        _owner.Quests.ActiveQuests[quest.TemplateId] = quest;
        var acts = quest.CurrentStep.Components[component.Id].Acts;
        quest.Objectives[0] = 1;
        quest.Objectives[1] = 1;
        quest.Objectives[2] = 1;
        acts.Single(act => act.Id == 1003).OverrideObjectiveCompleted = true;
        _world.SphereQuestManager.AddSphereQuestTrigger(new SphereQuestTrigger
        {
            Owner = _owner, Quest = quest,
            Sphere = new SphereQuest { ComponentId = 101, QuestId = 100, Radius = 10 }
        });

        _owner.ParentWorld = detachFromWorld ? null : _otherWorld;

        await Assert.That(_world.SphereQuestManager.GetSphereQuestTriggers()).IsEmpty();
        await Assert.That(acts.Single(act => act.Id == 1000).RunAct()).IsFalse();
        await Assert.That(acts.Single(act => act.Id == 1001).RunAct()).IsTrue();
        await Assert.That(acts.Single(act => act.Id == 1002).RunAct()).IsFalse();
        await Assert.That(acts.Single(act => act.Id == 1003).RunAct()).IsFalse();
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(quest.Objectives[1]).IsEqualTo(1);
        await Assert.That(quest.Objectives[2]).IsEqualTo(1);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(_owner.Quests.ActiveQuests[100]).IsSameReferenceAs(quest);
        await Assert.That(_entered).IsEqualTo(0);
        await Assert.That(_exited).IsEqualTo(0);
    }

    private SphereQuestTrigger CreateTrigger()
    {
        return new SphereQuestTrigger { Owner = _owner, Sphere = new SphereQuest { Radius = 10 }, TickRate = 0 };
    }

    private void SetRegionObjects(params GameObject[] objects)
    {
        SetField(_owner.Region, "_objects", objects);
        SetField(_owner.Region, "_objectsSize", objects.Length);
    }

    private void SetSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSingletons.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
