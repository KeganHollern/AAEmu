using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class DoodadPermissionTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private CharacterMock _player;
    private House _house;
    private Dictionary<uint, House> _houses;
    private Dictionary<uint, List<DoodadFunc>> _functions;
    private Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> _templates;
    private Dictionary<ulong, Item> _items;
    private Family _family;
    private Team _team;
    private RecordingFunction _function;
    private RecordingPhaseFunction _phase;
    private RecordingDoodad _doodad;

    [Before(Test)]
    public void SetUp()
    {
        var itemIds = Mock.Of<IItemIdManager>();
        itemIds.GetNextId().Returns(1000U);
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, itemIds.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        Install(items);
        _items = [];
        SetField(items, "_allItems", _items);
        SetField(items, "_templates", new Dictionary<uint, ItemTemplate>());
        var containers = new Dictionary<ulong, ItemContainer>();
        _player = new CharacterMock { Id = 2, AccountId = 20, NumInventorySlots = 10, NumBankSlots = 10 };
        ulong containerId = 1;
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_player.Id, slotType, false, _player)
                { ContainerId = containerId++, Owner = _player };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _player.Inventory = new Inventory(_player);
        var skills = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate>());
        Install(skills);
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            items, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        _function = new RecordingFunction();
        _phase = new RecordingPhaseFunction();
        _functions = new Dictionary<uint, List<DoodadFunc>>
        {
            [10] = [new DoodadFunc { FuncId = 1, GroupId = 10, FuncType = nameof(RecordingFunction), SkillId = 40,
                PermId = 1, NextPhase = 11 }],
            [11] = []
        };
        _templates = new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(RecordingFunction)] = new() { [1] = _function },
            [nameof(DoodadFuncRecoverItem)] = new() { [1] = new DoodadFuncRecoverItem() }
        };
        SetField(manager, "_funcsByGroups", _functions);
        SetField(manager, "_funcTemplates", _templates);
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [10] = [new DoodadPhaseFunc { FuncId = 1, GroupId = 10, FuncType = nameof(RecordingPhaseFunction) }]
        });
        SetField(manager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(RecordingPhaseFunction)] = new() { [1] = _phase }
        });
        Install(manager);
        var housing = new HousingManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object, Mock.Of<IWorldManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IHousingIdManager>().Object, Mock.Of<IHousingTldManager>().Object,
            items, Mock.Of<IMailManager>().Object, Mock.Of<INameManager>().Object,
            Mock.Of<IZoneManager>().Object, manager, Mock.Of<IUccManager>().Object);
        _house = new House { Id = 7, OwnerId = 1, AccountId = 10,
            Template = new HousingTemplate { HousingBindingDoodad = [] }, CurrentStep = -1,
            Permission = HousingPermission.Private };
        _houses = new Dictionary<uint, House> { [_house.Id] = _house };
        SetField(housing, "_houses", _houses);
        Install(housing);
        var names = new NameManager();
        SetField(names, "_characterAccounts", new Dictionary<uint, uint> { [1] = 10, [2] = 20 });
        Install(names);
        var families = new FamilyManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object,
            Mock.Of<IFamilyIdManager>().Object);
        _family = new Family { Id = 30 };
        _family.Members.Add(new FamilyMember { Id = 1 });
        SetField(families, "_families", new Dictionary<uint, Family> { [30] = _family });
        Install(families);
        var teams = new TeamManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object,
            Mock.Of<ITeamIdManager>().Object);
        _team = new Team { Id = 1, IsParty = false };
        _team.Members[0] = new TeamMember(_player);
        _team.Members[1] = new TeamMember(new CharacterMock { Id = 1 });
        SetField(teams, "_activeTeams", new ConcurrentDictionary<uint, Team>(new Dictionary<uint, Team> { [1] = _team }));
        Install(teams);
        var zones = new ZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<ITaskManager>().Object);
        SetField(zones, "_zones", new Dictionary<uint, Zone>
        {
            [10] = new() { ZoneKey = 10, GroupId = 50 },
            [11] = new() { ZoneKey = 11, GroupId = 50 },
            [12] = new() { ZoneKey = 12, GroupId = 51 }
        });
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
        Install(zones);
        _doodad = new RecordingDoodad { OwnerId = 1, OwnerType = DoodadOwnerType.Character, FuncGroupId = 10 };
        _doodad.Transform.ZoneId = 10;
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(0u, true)]
    [Arguments(1u, false)]
    [Arguments(2u, false)]
    [Arguments(3u, false)]
    [Arguments(4u, true)]
    [Arguments(5u, true)]
    [Arguments(6u, false)]
    [Arguments(7u, false)]
    [Arguments(8u, false)]
    [Arguments(256u, false)]
    public async Task Allows_EachNativePermission_UsesCurrentMembership(uint permission, bool allowed)
    {
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, permission)).IsEqualTo(allowed);
    }

    [Test]
    [Arguments(DoodadFuncPermission.OwnerOnly)]
    [Arguments(DoodadFuncPermission.OwnerFamily)]
    [Arguments(DoodadFuncPermission.OwnerParty)]
    [Arguments(DoodadFuncPermission.OwnerRaidMembers)]
    [Arguments(DoodadFuncPermission.SameAccount)]
    public async Task Allows_Owner_HasPersonalPermission(DoodadFuncPermission permission)
    {
        _doodad.OwnerId = _player.Id;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, (uint)permission)).IsTrue();
    }

    [Test]
    public async Task Allows_OfflineOwnerFamilyAndAccount_UsesSavedIdentity()
    {
        _player.Family = 30;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 2)).IsTrue();
        _family.Members.Clear();
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 2)).IsFalse();
        _player.AccountId = 10;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 6)).IsTrue();
        _doodad.OwnerId = 999;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 6)).IsFalse();
    }

    [Test]
    public async Task Allows_PartyPermission_UsesFiveMemberRaidSubgroup()
    {
        _team.Members[5] = _team.Members[1];
        _team.Members[1] = null;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 4)).IsFalse();
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 5)).IsTrue();
        _team.Members[5] = null;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 5)).IsFalse();
    }

    [Test]
    [Arguments(255, false, 50u, true)]
    [Arguments(1, true, 50u, true)]
    [Arguments(1, false, 50u, false)]
    [Arguments(1, true, 51u, false)]
    [Arguments(1, true, 0u, true)]
    public async Task Allows_SiegeMaster_UsesRolePolicyAndDoodadExpedition(int role, bool siegeMaster, uint expedition, bool allowed)
    {
        _player.Expedition = new Expedition
        {
            Id = (FactionsEnum)50,
            Members = [new ExpeditionMember { CharacterId = _player.Id, Role = (byte)role }],
            Policies = [new ExpeditionRolePolicy { Role = (byte)role, SiegeMaster = siegeMaster }]
        };
        var doodad = new Doodad { OwnerId = 1, Type2 = expedition };
        await Assert.That(DoodadPermissionRules.Allows(_player, doodad, 3)).IsEqualTo(allowed);
    }

    [Test]
    public async Task Allows_ZoneResident_UsesAccountAndZoneGroup()
    {
        _house.AccountId = _player.AccountId;
        _house.Transform.ZoneId = 11;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 8)).IsTrue();
        _house.Transform.ZoneId = 12;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 8)).IsFalse();
        _house.Transform.ZoneId = 999;
        _doodad.Transform.ZoneId = 999;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 8)).IsFalse();
    }

    [Test]
    public async Task Allows_HouseOwnerChanged_IgnoresPreviousDoodadOwner()
    {
        _doodad.OwnerType = DoodadOwnerType.Housing;
        _doodad.OwnerDbId = _house.Id;
        _house.OwnerId = _player.Id;
        _house.AccountId = _player.AccountId;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 1)).IsTrue();
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 6)).IsTrue();
        _houses.Clear();
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 1)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Use_DeniedFunction_DoesNotRunFunctionOrPhase(bool direct)
    {
        if (direct)
            _doodad.DoFunc(_player, 40, _functions[10][0]);
        else
            _doodad.Use(_player, 40);
        await Assert.That(_function.Calls).IsEqualTo(0);
        await Assert.That(_phase.Calls).IsEqualTo(0);
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(10u);
        await Assert.That(_doodad.DeleteCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecoverItem_DeniedHouseAccess_PreservesDoodadAndAttachedItem(bool templateOnly)
    {
        var item = new ItemMock(99, new ItemTemplate { Id = 100, MaxCount = 1 })
        {
            OwnerId = 1, Count = 1, SlotType = SlotType.System, _holdingContainer = _player.Inventory.SystemContainer
        };
        _items.Add(item.Id, item);
        _player.Inventory.SystemContainer.Items.Add(item);
        _doodad.ItemId = templateOnly ? 0UL : item.Id;
        _doodad.ItemTemplateId = item.TemplateId;
        _doodad.OwnerType = DoodadOwnerType.Housing;
        _doodad.OwnerDbId = _house.Id;
        _doodad.IsPersistent = true;
        _functions[10][0].FuncType = nameof(DoodadFuncRecoverItem);
        _functions[10][0].PermId = 0;

        new RecoverItem().Execute(_player, null, _doodad, null, 40, _doodad.ObjId);

        await Assert.That(_doodad.DeleteCalls).IsEqualTo(0);
        await Assert.That(_doodad.IsPersistent).IsTrue();
        await Assert.That(_doodad.ItemId).IsEqualTo(templateOnly ? 0UL : item.Id);
        await Assert.That(_doodad.ItemTemplateId).IsEqualTo(item.TemplateId);
        await Assert.That(item._holdingContainer).IsSameReferenceAs(_player.Inventory.SystemContainer);
        await Assert.That(_player.Inventory.SystemContainer.Items.Contains(item)).IsTrue();
        await Assert.That(_items[item.Id]).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(1);
        await Assert.That(_doodad.ToNextPhase).IsFalse();
    }

    [Test]
    public async Task RecoverItem_DeniedFunctionPermission_DoesNotDelete()
    {
        _functions[10][0].FuncType = nameof(DoodadFuncRecoverItem);
        _doodad.ItemTemplateId = 100;
        new RecoverItem().Execute(_player, null, _doodad, null, 40, _doodad.ObjId);
        await Assert.That(_doodad.DeleteCalls).IsEqualTo(0);
        await Assert.That(_doodad.ItemTemplateId).IsEqualTo(100u);
    }

    [Test]
    public async Task EndCraft_PermissionChangedDuringCast_DoesNotGrantProducts()
    {
        var worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(worldManager);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worldManager)!;
        worlds[world.Id] = world;
        _player.ParentWorld = world;
        _doodad.ObjId = 700;
        _doodad.ParentWorld = world;
        var doodads = (ConcurrentDictionary<uint, Doodad>)typeof(WorldInstance)
            .GetField("_doodads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        doodads[_doodad.ObjId] = _doodad;
        _player.Craft = new CharacterCraft(_player);
        SetField(_player.Craft, "<CurrentCraft>k__BackingField", new Craft());
        SetField(_player.Craft, "<DoodadId>k__BackingField", _doodad.ObjId);
        _doodad.OwnerId = _player.Id;
        await Assert.That(DoodadPermissionRules.Allows(_player, _doodad, 1)).IsTrue();
        _doodad.OwnerId = 1;

        _player.Craft.EndCraft();

        await Assert.That(_player.Craft.HasCurrentCraft).IsFalse();
        await Assert.That(_player.Inventory.Bag.Items).IsEmpty();
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingDoodad : Doodad
    {
        public int DeleteCalls { get; private set; }
        public override void Delete() => DeleteCalls++;
    }

    private sealed class RecordingFunction : DoodadFuncTemplate
    {
        public int Calls { get; private set; }
        public override void Use(AAEmu.Game.Models.Game.Units.BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
        {
            Calls++;
            owner.ToNextPhase = true;
        }
    }

    private sealed class RecordingPhaseFunction : DoodadPhaseFuncTemplate
    {
        public int Calls { get; private set; }
        public override bool Use(AAEmu.Game.Models.Game.Units.BaseUnit caster, Doodad owner)
        {
            Calls++;
            return true;
        }
    }
}
