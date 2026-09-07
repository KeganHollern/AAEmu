using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.FishSchools;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class FishConversionExchangeTests
{
    private const uint InputTemplate = 100;
    private const uint OutputTemplate = 101;
    private const uint SkillId = 22201;
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private ItemManager _items;
    private Dictionary<ulong, Item> _allItems;
    private List<ulong> _deleted;
    private Dictionary<uint, ItemTemplate> _templates;
    private Dictionary<uint, FishDetails> _fishDetails;
    private Dictionary<uint, List<DoodadFunc>> _functions;
    private ConcurrentDictionary<uint, Doodad> _doodads;
    private CharacterMock _character;
    private Doodad _owner;
    private WorldInstance _otherWorld;
    private Item _pack;
    private DoodadFuncConvertFish _conversion;

    [Before(Test)]
    public void SetUp()
    {
        var ids = Mock.Of<IItemIdManager>();
        ids.GetNextId().Returns(1_000U);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        Install(_items);
        Install(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _templates = new Dictionary<uint, ItemTemplate>
        {
            [InputTemplate] = new() { Id = InputTemplate, MaxCount = 1 },
            [OutputTemplate] = new() { Id = OutputTemplate, MaxCount = 1 }
        };
        _allItems = [];
        _deleted = [];
        SetField(_items, "_templates", _templates);
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_removedItems", _deleted);
        var loot = new LootGameData();
        SetField(loot, "_lootPacks", new Dictionary<uint, LootPack>
        {
            [10] = new()
            {
                Id = 10,
                Loots = [new Loot { ItemId = OutputTemplate, MinAmount = 1, MaxAmount = 1, DropRate = 10_000_000 }]
            }
        });
        Install(loot);
        _fishDetails = new Dictionary<uint, FishDetails>
        {
            [InputTemplate] = new() { ItemId = InputTemplate, MinLength = 20, MaxLength = 20, MinWeight = 50, MaxWeight = 50 }
        };
        var fishData = new FishDetailsGameData();
        SetField(fishData, "_fishDetails", _fishDetails);
        Install(fishData);
        var skills = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate>
        {
            [SkillId] = new() { Id = SkillId, MinRange = 0, MaxRange = 4 }
        });
        Install(skills);
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        Install(models);
        var doodads = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            _items, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        _functions = [];
        SetField(doodads, "_funcsByGroups", _functions);
        SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        Install(doodads);

        var worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(worldManager);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        _otherWorld = new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 2);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worldManager)!;
        worlds[world.Id] = world;
        worlds[_otherWorld.Id] = _otherWorld;
        _character = new CharacterMock { Id = 7, NumInventorySlots = 10, NumBankSlots = 10, ParentWorld = world };
        var containers = new Dictionary<ulong, ItemContainer>();
        ulong containerId = 1;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_character.Id, slotType, false, _character)
                { ContainerId = containerId++, Owner = _character };
            containers.Add(container.ContainerId, container);
        }
        SetField(_items, "_allPersistentContainers", containers);
        _character.Inventory = new Inventory(_character);
        _owner = new Doodad { ObjId = 500, ParentWorld = world };
        _doodads = (ConcurrentDictionary<uint, Doodad>)typeof(WorldInstance)
            .GetField("_doodads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        _doodads[_owner.ObjId] = _owner;
        _pack = new ProtectedPack(1, _templates[InputTemplate]) { Detail = [1, 2, 3, 4] };
        PutPack(_pack);
        SelectFunction(1, 17729);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    [Arguments(1u, 17729u)]
    [Arguments(2u, 17262u)]
    [Arguments(4u, 19948u)]
    public async Task EachDeployedFunction_ExchangesPackOnceAndPreservesTrophyDetails(uint functionId, uint groupId)
    {
        if (functionId != 1)
            SelectFunction(functionId, groupId);
        var observations = new List<bool>();
        _character.Events.OnItemGather += (_, _) =>
        {
            observations.Add(_character.Equipment.Items.Count == 0 &&
                _character.Inventory.Bag.Items.Count == 1 && !_allItems.ContainsKey(_pack.Id));
            _conversion.Use(_character, _owner, SkillId);
        };

        _conversion.Use(_character, _owner, SkillId);
        _conversion.Use(_character, _owner, SkillId);

        await Assert.That(_character.Equipment.Items).IsEmpty();
        var trophy = (BigFish)_character.Inventory.Bag.Items.Single();
        await Assert.That(trophy.TemplateId).IsEqualTo(OutputTemplate);
        await Assert.That(trophy.Count).IsEqualTo(1);
        await Assert.That(trophy.Length).IsEqualTo(20f);
        await Assert.That(trophy.Weight).IsEqualTo(50f);
        await Assert.That(trophy.Detail.Length).IsEqualTo(16);
        await Assert.That(BitConverter.ToSingle(trophy.Detail, 0)).IsEqualTo(trophy.Weight);
        await Assert.That(BitConverter.ToSingle(trophy.Detail, 4)).IsEqualTo(trophy.Length);
        await Assert.That(_allItems.Keys).IsEquivalentTo([1_000UL]);
        await Assert.That(_deleted).IsEquivalentTo([1UL]);
        await Assert.That(observations.Count > 0 && observations.All(observed => observed)).IsTrue();
    }

    [Test]
    public async Task FullBag_PreservesExactPackAndReleasesPreparedTrophyWithoutCallbacks()
    {
        _character.Inventory.Bag.ContainerSize = 0;
        _pack.IsDirty = false;
        var notifications = 0;
        _character.Events.OnItemGather += (_, _) => notifications++;

        _conversion.Use(_character, _owner, SkillId);

        await AssertPackPreserved();
        await Assert.That(_pack.IsDirty).IsFalse();
        await Assert.That(_deleted).IsEquivalentTo([1_000UL]);
        await Assert.That(notifications).IsEqualTo(0);
    }

    [Test]
    public async Task FailedPackRemoval_RestoresBagAndReleasesPreparedTrophy()
    {
        ((ProtectedPack)_pack).AllowDestroy = false;
        var notifications = 0;
        _character.Events.OnItemGather += (_, _) => notifications++;

        _conversion.Use(_character, _owner, SkillId);

        await AssertPackPreserved();
        await Assert.That(_deleted).IsEquivalentTo([1_000UL]);
        await Assert.That(notifications).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task InvalidFishDetails_RejectBeforeAllocatingTrophy(int invalidCase)
    {
        var details = _fishDetails[InputTemplate];
        switch (invalidCase)
        {
            case 0: _fishDetails.Clear(); break;
            case 1: details.MinLength = -1; break;
            case 2: details.MaxLength = 0; break;
            case 3: details.MinLength = details.MaxLength + 1; break;
            case 4: details.MinWeight = details.MaxWeight + 1; break;
        }

        _conversion.Use(_character, _owner, SkillId);

        await AssertPackPreserved();
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task InvalidInteraction_RejectsBeforeInventoryMutation(int invalidCase)
    {
        switch (invalidCase)
        {
            case 0: _doodads.Clear(); break;
            case 1: _owner.Despawn = DateTime.UtcNow; break;
            case 2: _character.ParentWorld = _otherWorld; break;
            case 3: _functions[_owner.FuncGroupId][0].FuncId = 999; break;
            case 4: _functions[_owner.FuncGroupId][0].FuncType = nameof(DoodadFuncBuyFish); break;
            case 5: _owner.Transform.Local.SetPosition(10, 0, 0); break;
        }

        _conversion.Use(_character, _owner, SkillId);

        await AssertPackPreserved();
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingMappingOrOutputTemplate_PreservesPackBeforeAllocation(bool missingOutput)
    {
        if (missingOutput)
            _templates.Remove(OutputTemplate);
        else
            _pack.TemplateId = 999;

        _conversion.Use(_character, _owner, SkillId);

        await AssertPackPreserved();
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    public async Task UnknownSkill_RejectsBeforeInventoryMutation()
    {
        _conversion.Use(_character, _owner, 999);

        await AssertPackPreserved();
        await Assert.That(_deleted).IsEmpty();
    }

    [Test]
    [Arguments(4f, true)]
    [Arguments(4.01f, false)]
    public async Task ConversionSkillRange_UsesInclusiveFourMeterBoundary(float distance, bool succeeds)
    {
        _owner.Transform.Local.SetPosition(distance, 0, 0);

        _conversion.Use(_character, _owner, SkillId);

        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(succeeds ? 1 : 0);
        await Assert.That(_character.Equipment.Items.Count).IsEqualTo(succeeds ? 0 : 1);
    }

    [Test]
    public async Task ConcurrentRequests_CannotConsumeThePackTwice()
    {
        await Task.WhenAll(Task.Run(() => _conversion.Use(_character, _owner, SkillId)),
            Task.Run(() => _conversion.Use(_character, _owner, SkillId)));

        await Assert.That(_character.Inventory.Bag.Items.Count).IsEqualTo(1);
        await Assert.That(_character.Equipment.Items).IsEmpty();
        await Assert.That(_allItems.Keys).IsEquivalentTo([1_000UL]);
    }

    private async Task AssertPackPreserved()
    {
        await Assert.That(_character.Equipment.Items.Single()).IsSameReferenceAs(_pack);
        await Assert.That(_pack.Count).IsEqualTo(1);
        await Assert.That(_pack._holdingContainer).IsSameReferenceAs(_character.Equipment);
        await Assert.That(_pack.Slot).IsEqualTo((int)EquipmentItemSlot.Backpack);
        await Assert.That(_pack.Detail).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(_character.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_allItems.Keys).IsEquivalentTo([1UL]);
    }

    private void SelectFunction(uint functionId, uint groupId)
    {
        _conversion = new DoodadFuncConvertFish { Id = functionId };
        _functions[groupId] = [new DoodadFunc { FuncId = functionId, FuncType = nameof(DoodadFuncConvertFish), SkillId = SkillId }];
        _owner.FuncGroupId = groupId;
        _items.AddFishConversion(new LootPackConvertFish
        {
            Id = functionId, DoodadFuncConvertFishId = functionId, ItemId = InputTemplate, LootPackId = 10
        });
    }

    private void PutPack(Item item)
    {
        item.OwnerId = _character.Id;
        item.SlotType = SlotType.Equipment;
        item.Slot = (int)EquipmentItemSlot.Backpack;
        item._holdingContainer = _character.Equipment;
        _character.Equipment.Items.Add(item);
        _character.Equipment.UpdateFreeSlotCount();
        _allItems.Add(item.Id, item);
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class ProtectedPack(uint id, ItemTemplate template) : ItemMock(id, template)
    {
        public bool AllowDestroy { get; set; } = true;
        public override bool CanDestroy() => AllowDestroy;
    }
}
