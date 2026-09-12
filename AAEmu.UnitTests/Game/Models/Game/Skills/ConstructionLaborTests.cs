using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class ConstructionLaborTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private ConstructionCharacter _owner;
    private Dictionary<ulong, Item> _items;
    private TaskManager _tasks;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new AccountManager(null, null, TimeProvider.System));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
        SetInstance(formulas);
        SetInstance(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _tasks = new TaskManager(null);
        SetInstance(_tasks);
        var ids = Mock.Of<IItemIdManager>();
        ids.GetNextId().Returns(1000U);
        var itemManager = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            _tasks, Mock.Of<IWorldManager>().Object);
        SetInstance(itemManager);
        _items = [];
        SetField(itemManager, "_allItems", _items);
        SetField(itemManager, "_removedItems", new List<ulong>());
        SetField(itemManager, "_templates", new Dictionary<uint, ItemTemplate>
            { [200] = new() { Id = 200, MaxCount = 1, FixedGrade = 0 } });
        _owner = new ConstructionCharacter { Id = 7, AccountId = 11, NumInventorySlots = 10 };
        _owner.InitializeLaborCache(20, DateTime.UtcNow);
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_owner.Id, type, false, _owner)
                { Owner = _owner, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(itemManager, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
        _previous.Clear();
    }

    [Test]
    [Arguments(false, "success")]
    [Arguments(true, "success")]
    [Arguments(false, "no_labor")]
    [Arguments(true, "no_labor")]
    [Arguments(false, "failed_commit")]
    [Arguments(true, "failed_commit")]
    public async Task House_ProgressAndCompletionShareTheLaborOutcome(bool final, string outcome)
    {
        var template = new HousingTemplate { MainModelId = 90, HousingBindingDoodad = [] };
        template.BuildSteps.Add(0, new HousingBuildStep { NumActions = 2, SkillId = 50, ModelId = 10 });
        if (!final)
            template.BuildSteps.Add(1, new HousingBuildStep { NumActions = 3, SkillId = 51, ModelId = 20 });
        var house = new House { Template = template, CurrentStep = 0, NumAction = 1, IsDirty = false };
        var door = new SpawnProbe();
        house.AttachedDoodads.Add(door);
        var skill = NewSkill(outcome);
        var observedBeforeCommit = false;
        skill.CommitLaborBatch = (_, _) =>
        {
            observedBeforeCommit = house.CurrentStep == (final ? -1 : 1) &&
                house.ModelId == (final ? 90U : 20U) && door.Spawns == 0 && _owner.Packets.Count == 0;
            return outcome != "failed_commit";
        };

        var result = SkillLaborBatch.Run(_owner, skill, true,
            () => CraftEffect.AdvanceHouseConstruction(_owner, house, 50, skill));

        var success = outcome == "success";
        await Assert.That(result).IsEqualTo(success);
        await Assert.That(observedBeforeCommit).IsEqualTo(outcome != "no_labor");
        await Assert.That(house.CurrentStep).IsEqualTo(success ? (final ? -1 : 1) : 0);
        await Assert.That(house.NumAction).IsEqualTo(success ? 0 : 1);
        await Assert.That(house.CurrentAction).IsEqualTo(success ? (final ? 0 : 2) : 1);
        await Assert.That(house.ModelId).IsEqualTo(success ? (final ? 90U : 20U) : 10U);
        await Assert.That(house.IsDirty).IsEqualTo(success);
        await Assert.That(door.Spawns).IsEqualTo(success && final ? 1 : 0);
        await Assert.That(_owner.Packets.Count).IsEqualTo(success ? 1 : 0);
        await Assert.That(_owner.LaborPower).IsEqualTo(outcome == "no_labor" ? (short)0 : success ? (short)10 : (short)20);
    }

    [Test]
    [Arguments(false, "success")]
    [Arguments(true, "success")]
    [Arguments(false, "no_labor")]
    [Arguments(true, "no_labor")]
    [Arguments(false, "failed_commit")]
    [Arguments(true, "failed_commit")]
    public async Task Shipyard_RestoresStepModelAndBothActionCountersOnFailure(bool final, string outcome)
    {
        var shipyard = NewShipyard(final);
        var skill = NewSkill(outcome);
        var observedBeforeCommit = false;
        skill.CommitLaborBatch = (_, _) =>
        {
            observedBeforeCommit = shipyard.CurrentStep == (final ? -1 : 1) &&
                shipyard.ShipyardData.Step == 1 && _owner.Packets.Count == 0;
            return outcome != "failed_commit";
        };

        var result = SkillLaborBatch.Run(_owner, skill, true,
            () => CraftEffect.AdvanceShipyardConstruction(_owner, shipyard, 50, skill));

        var success = outcome == "success";
        await Assert.That(result).IsEqualTo(success);
        await Assert.That(observedBeforeCommit).IsEqualTo(outcome != "no_labor");
        await Assert.That(shipyard.CurrentStep).IsEqualTo(success ? (final ? -1 : 1) : 0);
        await Assert.That(shipyard.NumAction).IsEqualTo(success ? 0 : 1);
        await Assert.That(shipyard.BaseAction).IsEqualTo(success && !final ? 2 : 0);
        await Assert.That(shipyard.ModelId).IsEqualTo(success ? (final ? 90U : 20U) : 10U);
        await Assert.That(shipyard.ShipyardData.Step).IsEqualTo(success ? 1 : 0);
        await Assert.That(shipyard.ShipyardData.Actions).IsEqualTo(success ? 2 : 1);
        await Assert.That(shipyard.IsDirty).IsEqualTo(success);
        await Assert.That(_owner.Packets.Count).IsEqualTo(success ? 1 : 0);
        await Assert.That(_owner.LaborPower).IsEqualTo(outcome == "no_labor" ? (short)0 : success ? (short)10 : (short)20);
    }

    [Test]
    [Arguments("success")]
    [Arguments("no_labor")]
    [Arguments("failed_commit")]
    [Arguments("full_bag")]
    [Arguments("foreign_owner")]
    public async Task ShipLaunch_ItemAndCeremonyStartOnlyAfterPayment(string outcome)
    {
        var shipyard = NewShipyard(true);
        shipyard.AddBuildAction();
        shipyard.ShipyardData.Step = 1;
        shipyard.ShipyardData.Actions = 2;
        if (outcome == "full_bag")
            _owner.Inventory.Bag.ContainerSize = 0;
        if (outcome == "foreign_owner")
            shipyard.ShipyardData.Type2 = 8;
        var skill = NewSkill(outcome);

        var result = SkillLaborBatch.Run(_owner, skill, true,
            () => CraftEffect.CompleteShipyardConstruction(_owner, shipyard, skill));

        var success = outcome == "success";
        var queue = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)GetField(_tasks, "_queue");
        await Assert.That(result).IsEqualTo(success);
        await Assert.That(shipyard.ShipyardData.Step).IsEqualTo(success ? 1000 : 1);
        await Assert.That(_owner.Inventory.Bag.Items.Count).IsEqualTo(success ? 1 : 0);
        await Assert.That(queue.Count).IsEqualTo(success ? 1 : 0);
        await Assert.That(_owner.Packets.Count).IsEqualTo(success ? 1 : 0);
        await Assert.That(_owner.LaborPower).IsEqualTo(outcome == "no_labor" ? (short)0 : success ? (short)10 : (short)20);
    }

    [Test]
    public async Task WrongConstructionSkill_PreservesLaborAndProgress()
    {
        var shipyard = NewShipyard(true);
        var skill = NewSkill("success");
        await Assert.That(SkillLaborBatch.Run(_owner, skill, true,
            () => CraftEffect.AdvanceShipyardConstruction(_owner, shipyard, 99, skill))).IsFalse();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(shipyard.NumAction).IsEqualTo(1);
        await Assert.That(_owner.Packets).IsEmpty();
    }

    private Shipyard NewShipyard(bool final)
    {
        var template = new ShipyardsTemplate { MainModelId = 90, ItemId = 200, CeremonyAnimTime = 5000 };
        template.ShipyardSteps.Add(0, new ShipyardSteps { NumActions = 2, SkillId = 50, ModelId = 10 });
        if (!final)
            template.ShipyardSteps.Add(1, new ShipyardSteps { NumActions = 3, SkillId = 51, ModelId = 20 });
        var shipyard = new Shipyard { Template = template, ModelId = 10,
            ShipyardData = new ShipyardData { Type2 = _owner.Id, Step = 0, Actions = 1 } };
        shipyard.AddBuildAction();
        shipyard.IsDirty = false;
        return shipyard;
    }

    private Skill NewSkill(string outcome)
    {
        if (outcome == "no_labor")
            _owner.InitializeLaborCache(0, DateTime.UtcNow);
        return new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 })
            { CommitLaborBatch = (_, _) => outcome != "failed_commit" };
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static object GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class ConstructionCharacter : CharacterMock
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }

    private sealed class SpawnProbe : Doodad
    {
        public int Spawns { get; private set; }
        public override void Spawn() => Spawns++;
    }
}
