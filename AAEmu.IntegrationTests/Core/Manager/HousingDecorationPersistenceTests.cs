using System.Numerics;
using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false, false, "success")]
    [InlineData(false, false, "failed_commit")]
    [InlineData(true, false, "success")]
    [InlineData(true, false, "failed_commit")]
    [InlineData(false, true, "success")]
    [InlineData(false, true, "failed_commit")]
    [InlineData(false, false, "wrong_design")]
    [InlineData(false, false, "family")]
    [InlineData(false, false, "guild")]
    [InlineData(false, false, "reserved")]
    [InlineData(false, false, "bank")]
    [InlineData(false, false, "cross_world")]
    [InlineData(false, false, "cap_full")]
    [InlineData(false, false, "crop_in_last_slot")]
    [InlineData(false, false, "bound_door_at_cap")]
    [InlineData(false, false, "unfinished")]
    [InlineData(false, false, "range")]
    [InlineData(false, false, "outside_bounds")]
    [InlineData(false, false, "overlap")]
    [InlineData(false, false, "no_support")]
    [InlineData(false, false, "forged_parent")]
    public Task HouseDecoration_OnlyCommitsExactItemAndDoodadTogether(bool stackable, bool coffer, string outcome)
        => CheckDecoration(stackable, coffer, outcome);

    [Theory]
    [InlineData(31878u, 520u, 7821u, 21490u, 1155u, true)]
    [InlineData(31878u, 520u, 7821u, 21491u, 1156u, true)]
    [InlineData(27656u, 335u, 6573u, 17397u, 750u, false)]
    public Task OwnerCollect_ReloadsPlacedFurnitureAndRecoversTheSameItem(uint itemId, uint designId,
        uint templateId, uint phase, uint function, bool restore)
        => CheckDecoration(false, false, "success", new(itemId, designId, templateId, phase, function, restore));

    private sealed record RecoveryCase(uint Item, uint Design, uint Template, uint Phase, uint Function, bool Restore);

    private static async Task CheckDecoration(bool stackable, bool coffer, string outcome, RecoveryCase recovery = null)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var reservation = new TradeReservation();
        var zones = new ZoneManager(null, null);
        SetField(zones, "_zones", new Dictionary<uint, Zone> { [10] = new() { ZoneKey = 10, GroupId = 50 } });
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
        var oldZones = SwapSingleton(zones);
        var oldHousingData = SwapSingleton(new HousingGameData());
        var oldWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, ExpRate = 1 };
        var containerIdField = typeof(ContainerIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldContainerIds = containerIdField.GetValue(null);
        var containerIds = new ContainerIdManager();
        Assert.True(containerIds.Initialize());
        containerIdField.SetValue(null, containerIds);
        try
        {
            var player = graph.Sender;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            Item AddItem(byte slot) => recovery != null ? graph.AddItem(slot, recovery.Item) :
                stackable ? graph.AddItem(slot) : graph.AddEquipment(slot);
            var designId = recovery?.Design ?? 1u;
            var templateId = recovery?.Template ?? 1u;
            var item = AddItem(0);
            item.Count = stackable ? 2 : 1;
            // The same template in another slot must never substitute for the requested item identity.
            var alternative = AddItem(1);
            var alternativeCount = alternative.Count;
            SetField(HousingGameData.Instance, "_housingDecorations", new Dictionary<uint, HousingDecoration>
                { [designId] = new() { Id = designId, DoodadId = templateId, AllowOnFloor = true } });
            SetField(HousingGameData.Instance, "_housingItemHousingDecorations", new List<ItemHousingDecoration>
                { new() { DesignId = designId, ItemId = item.TemplateId, Restore = recovery?.Restore ?? false } });
            if (outcome == "bank")
            {
                player.Inventory.Bag.Items.Remove(item);
                item._holdingContainer = player.Inventory.Warehouse;
                item.SlotType = SlotType.Bank;
                player.Inventory.Warehouse.Items.Add(item);
            }
            if (outcome == "reserved")
                Assert.True(reservation.TryReserve(item, item.Count));
            Assert.True(graph.Save.TryCommitEconomy([player]));

            var world = HousingPlacementWorld();
            SetParentWorld(player, world);
            player.Transform.Local.Position = new Vector3(100, 200, 300);
            if (outcome == "range")
                player.Transform.Local.Position = new Vector3(113, 200, 300);
            player.Transform.ZoneId = 10;
            var house = new House
            {
                Id = player.Id + 30, TlId = 7, ObjId = player.Id + 31,
                OwnerId = outcome is "family" or "guild" ? graph.Receiver.Id : player.Id,
                AccountId = outcome is "family" or "guild" ? graph.Receiver.AccountId : player.AccountId,
                Template = new HousingTemplate { MainModelId = 1, HousingBindingDoodad = [], DecoLimit = 10,
                    AbsoluteDecoLimit = outcome is "cap_full" or "crop_in_last_slot" or "bound_door_at_cap" ? 1u : 10u }, CurrentStep = -1,
                Permission = outcome == "family" ? HousingPermission.Family :
                    outcome == "guild" ? HousingPermission.Guild : HousingPermission.Private
            };
            if (outcome == "unfinished")
            {
                house.Template.BuildSteps[0] = new HousingBuildStep();
                house.CurrentStep = 0;
            }
            SetParentWorld(house, outcome == "cross_world" ? HousingPlacementWorld(2) : world);
            house.Transform.Local.Position = new Vector3(100, 200, 300);
            house.Transform.ZoneId = 10;
            if (house.Template.AbsoluteDecoLimit == 1)
            {
                var existing = new Doodad
                {
                    ObjId = 255, TemplateId = outcome == "bound_door_at_cap" ? 4278u : 1u,
                    ItemTemplateId = outcome == "crop_in_last_slot" ? 15182u : item.TemplateId,
                    AttachPoint = outcome == "bound_door_at_cap" ? AttachPointKind.HealPoint0 : AttachPointKind.None,
                    OwnerDbId = house.Id, OwnerType = DoodadOwnerType.Housing, ParentObj = house, ParentObjId = house.ObjId
                };
                ((ConcurrentDictionary<uint, Doodad>)typeof(WorldInstance)
                    .GetField("_doodads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!)
                    [existing.ObjId] = existing;
            }
            var objectIds = new Mock<IObjectIdManager>();
            objectIds.Setup(ids => ids.GetNextId()).Returns(256);
            var doodadIds = new Mock<IDoodadIdManager>();
            var dbId = player.Id + 50;
            doodadIds.Setup(ids => ids.GetNextId()).Returns(dbId);
            HousingManager housing = null;
            var doodads = new DoodadManager(objectIds.Object, doodadIds.Object, graph.Items,
                new Lazy<IHousingManager>(() => housing), null);
            DoodadTemplate template = coffer ? new DoodadCofferTemplate { Id = 1, Capacity = 10 } : new DoodadTemplate { Id = templateId };
            template.Model = "cgf://housing-test-decoration.cgf";
            SetField(doodads, "_templates", new Dictionary<uint, DoodadTemplate> { [templateId] = template });
            if (recovery != null)
            {
                template.FuncGroups.Add(new DoodadFuncGroups { Id = recovery.Phase, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start });
                var collect = new DoodadFuncRecoverItem { Id = recovery.Function };
                SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
                SetField(doodads, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> { [recovery.Phase] =
                    [new() { FuncId = collect.Id, FuncType = nameof(DoodadFuncRecoverItem), NextPhase = -1 }] });
                SetField(doodads, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
                    { [nameof(DoodadFuncRecoverItem)] = new() { [collect.Id] = collect } });
            }
            var oldDoodads = SwapSingleton(doodads);
            housing = new HousingManager(objectIds.Object, Mock.Of<IFactionManager>(), Mock.Of<ILocalizationManager>(),
                Mock.Of<IWorldManager>(), Mock.Of<ITaskManager>(), Mock.Of<ISkillManager>(), Mock.Of<IHousingIdManager>(),
                Mock.Of<IHousingTldManager>(), graph.Items, graph.Mails, Mock.Of<INameManager>(), zones, doodads,
                Mock.Of<IUccManager>(), new Lazy<ISaveManager>(() => graph.Save), doodadIds.Object);
            ConfigureHousingGeometry(housing, outcome != "no_support", outcome == "overlap");
            SetField(housing, "_houses", new Dictionary<uint, House> { [house.Id] = house });
            SetField(housing, "_housesTl", new Dictionary<ushort, House> { [house.TlId] = house });
            var oldHousing = SwapSingleton(housing);
            Doodad published = null;
            var publications = 0;
            housing.PublishDecoration = doodad =>
            {
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={dbId}"));
                Assert.Equal(stackable ? 1 : (long)SlotType.System,
                    Scalar($"SELECT {(stackable ? "count" : "slot_type")} FROM items WHERE id={item.Id}"));
                publications++;
                published = doodad;
            };
            var trigger = $"decoration_fail_{player.Id}";
            if (outcome == "failed_commit")
                Execute($"CREATE TRIGGER {trigger} AFTER INSERT ON doodads FOR EACH ROW BEGIN IF NEW.id={dbId} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure after decoration row'; END IF; END");
            try
            {
                var result = housing.DecorateHouse(player, house.TlId, outcome == "wrong_design" ? 2u : designId,
                    outcome == "outside_bounds" ? new Vector3(9.75f, 2, 3) : new Vector3(1, 2, 3),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), outcome == "forged_parent" ? house.ObjId : 0, item.Id);
                var success = outcome is "success" or "crop_in_last_slot";
                Assert.Equal(success, result);
                Assert.Equal(success ? 1 : 0, publications);
                Assert.Equal(success ? 1 : 0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={dbId}"));
                Assert.Equal(20, player.LaborPower);
                Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
                Assert.Equal(10000, player.Money);
                Assert.Equal(10000, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
                Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
                Assert.Equal(alternativeCount, alternative.Count);
                Assert.Equal(alternativeCount, Scalar($"SELECT count FROM items WHERE id={alternative.Id}"));
                Assert.Same(player.Inventory.Bag, alternative._holdingContainer);
                Assert.Equal((long)SlotType.Inventory, Scalar($"SELECT slot_type FROM items WHERE id={alternative.Id}"));
                var expectedSlot = outcome == "bank" ? SlotType.Bank : success && !stackable ? SlotType.System : SlotType.Inventory;
                Assert.Equal(expectedSlot, item.SlotType);
                Assert.Equal((long)expectedSlot, Scalar($"SELECT slot_type FROM items WHERE id={item.Id}"));
                Assert.Equal(stackable ? success ? 1 : 2 : 1, item.Count);
                Assert.Equal(item.Count, Scalar($"SELECT count FROM items WHERE id={item.Id}"));
                if (success)
                {
                    Assert.NotNull(published);
                    Assert.Equal(stackable ? 0UL : item.Id, published.ItemId);
                    Assert.Equal(stackable ? 0 : (long)item.Id, Scalar($"SELECT item_id FROM doodads WHERE id={dbId}"));
                    Assert.Equal(item.TemplateId, published.ItemTemplateId);
                    Assert.Same(world, published.ParentWorld);
                    Assert.Same(house, published.ParentObj);
                    Assert.Same(house.Transform, published.Transform.Parent);
                    Assert.Equal(new Vector3(1, 2, 3), published.Transform.Local.Position);
                }
                else
                {
                    Assert.Null(published);
                    Assert.Empty(house.Transform.Children);
                    Assert.Empty(player.Inventory.SystemContainer.Items);
                }
                var containers = (Dictionary<ulong, ItemContainer>)typeof(ItemManager)
                    .GetField("_allPersistentContainers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
                var cofferContainers = containers.Values.OfType<CofferContainer>().ToList();
                Assert.Equal(coffer && success ? 1 : 0, cofferContainers.Count);
                Assert.Equal(coffer && success ? 1 : 0, Scalar($"SELECT COUNT(*) FROM item_containers WHERE owner_id={player.Id} AND container_type='CofferContainer'"));
                var reloaded = graph.ReloadLifecycle().Items.GetItemByItemId(item.Id);
                Assert.Equal(item.Id, reloaded.Id);
                Assert.Equal(item.Count, reloaded.Count);
                Assert.Equal(expectedSlot, reloaded.SlotType);
                if (!stackable && recovery == null)
                {
                    var equipment = Assert.IsType<EquipItem>(reloaded);
                    Assert.Equal(753u, equipment.UccId);
                    Assert.Equal(812u, equipment.ImageItemTemplateId);
                    Assert.Equal(4, equipment.Grade);
                    Assert.Equal(67, equipment.Durability);
                    Assert.Equal(91u, equipment.RuneId);
                    Assert.Equal(182u, equipment.DyeItemId);
                    Assert.Equal(new uint[] { 11, 22, 33, 0, 0, 0, 0 }, equipment.GemIds);
                    Assert.Equal(104, equipment.TemperPhysical);
                    Assert.Equal(103, equipment.TemperMagical);
                }
                var allocated = success || outcome == "failed_commit";
                objectIds.Verify(ids => ids.GetNextId(), allocated ? Times.Once() : Times.Never());
                doodadIds.Verify(ids => ids.GetNextId(), allocated ? Times.Once() : Times.Never());
                objectIds.Verify(ids => ids.ReleaseId(256), outcome == "failed_commit" ? Times.Once() : Times.Never());
                doodadIds.Verify(ids => ids.ReleaseId(dbId), outcome == "failed_commit" ? Times.Once() : Times.Never());
                if (recovery != null)
                    await CollectAfterReload(graph, house, item, published);
                if (outcome == "failed_commit")
                {
                    Execute($"DROP TRIGGER {trigger}");
                    Assert.True(housing.DecorateHouse(player, house.TlId, 1,
                        new Vector3(1, 2, 3), Quaternion.Identity, 0, item.Id));
                    Assert.Equal(1, publications);
                    Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={dbId}"));
                    Assert.Equal(stackable ? 1 : (long)SlotType.System,
                        Scalar($"SELECT {(stackable ? "count" : "slot_type")} FROM items WHERE id={item.Id}"));
                }
            }
            finally
            {
                if (outcome == "failed_commit")
                    Execute($"DROP TRIGGER IF EXISTS {trigger}");
                SwapSingleton(oldHousing);
                SwapSingleton(oldDoodads);
            }
        }
        finally
        {
            SwapSingleton(oldZones);
            SwapSingleton(oldHousingData);
            AppConfiguration.Instance.World = oldWorldConfig;
            containerIdField.SetValue(null, oldContainerIds);
        }
    }

    private static async Task CollectAfterReload(SendGraph graph, House house, Item placedItem, Doodad placed)
    {
        var items = graph.ReloadLifecycle().Items;
        var oldItems = SwapSingleton(items);
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        var oldModels = SwapSingleton(models);
        var oldGrant = SwapSingleton(new SkillGrantGameData());
        var oldRequirements = SwapSingleton(new SkillRequirementsGameData());
        var oldPermissions = SwapSingleton(new PermissionManager(Mock.Of<IAccountManager>()));
        var fresh = HousingPlacementWorld(house.ParentWorld.Id + 1);
        try
        {
            var owner = graph.Sender;
            owner.ParentWorld = house.ParentWorld = fresh;
            owner.ObjId = owner.Id;
            owner.Hp = owner.Mp = 100;
            fresh.AddObject(owner);
            owner.Inventory = new Inventory(owner);
            owner.Skills = new CharacterSkills(owner);
            owner.Abilities = new CharacterAbilities(owner);
            fresh.Regions = new Region[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL];
            var spawner = fresh.SpawnManager = new SpawnManager(fresh);
            Assert.Equal(1, spawner.SpawnPersistentDoodads(DoodadOwnerType.Housing, (int)house.Id, house, true));
            var furniture = Assert.Single(fresh.GetAllDoodads());
            Assert.Equal(placed.DbId, furniture.DbId);
            Assert.Equal(placed.FuncGroupId, furniture.FuncGroupId);
            var backing = items.GetItemByItemId(placedItem.Id);
            Assert.NotSame(placedItem, backing);
            Assert.Same(owner.Inventory.SystemContainer, backing._holdingContainer);
            Assert.Equal(backing.Id, furniture.ItemId);

            var template = new SkillTemplate
            {
                Id = 11361, TargetType = SkillTargetType.Doodad, TargetSelection = SkillTargetSelection.Target,
                CastingTime = 1000, MaxRange = 4, CastingDelayable = true, DefaultGcd = true, TargetAlive = true,
                Effects = [new SkillEffect
                {
                    ApplicationMethod = SkillEffectApplicationMethod.Target, StartLevel = 1, EndLevel = 100,
                    Friendly = true, NonFriendly = true, Front = true, Back = true, Chance = 100,
                    Template = new InteractionEffect { WorldInteraction = WorldInteractionType.RecoverItem }
                }]
            };
            SetField(SkillManager.Instance, "_comboFollowupSkills", new HashSet<uint>());
            HousingField<Dictionary<uint, SkillTemplate>>(SkillManager.Instance, "_skills")[template.Id] = template;
            var session = new Mock<ISession>();
            var connection = new GameConnection(session.Object) { ActiveChar = owner };
            owner.Connection = connection;
            void Collect()
            {
                var request = new PacketStream().Write(template.Id).Write((byte)0).WriteBc(owner.ObjId)
                    .Write((byte)SkillCastTargetType.Doodad).WriteBc(furniture.ObjId).Write((byte)0);
                new CSStartSkillPacket { Connection = connection }.Read(request);
            }

            owner.Transform.Local.Position = furniture.Transform.World.Position + Vector3.UnitX * 20;
            Collect();
            Assert.Null(owner.SkillTask);
            Assert.Same(owner.Inventory.SystemContainer, backing._holdingContainer);

            owner.Transform.Local.Position = furniture.Transform.World.Position;
            Collect();
            var cast = Assert.IsType<CastTask>(owner.SkillTask);
            Assert.Same(furniture, cast.Skill.InitialTarget);
            session.Verify(sender => sender.SendPacket(It.Is<byte[]>(packet => packet[packet.Length - 1] == (byte)SkillResult.TooFarRange)), Times.Once());
            await Task.Delay(TimeSpan.FromMilliseconds(template.CastingTime + 10));
            cast.Execute();
            Assert.Same(backing, owner.Inventory.Bag.GetItemByItemId(placedItem.Id));
            Assert.Equal(placedItem.TemplateId, backing.TemplateId);
            Assert.Equal(1, backing.Count);
            Assert.Empty(fresh.GetAllDoodads());

            using var database = AAEmu.Commons.Utils.DB.MySQL.CreateConnection();
            using var transaction = database.BeginTransaction();
            items.Save(database, transaction);
            transaction.Commit();
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={placedItem.Id} AND owner={owner.Id} AND slot_type={(int)SlotType.Inventory} AND count=1"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={placed.DbId} OR item_id={placedItem.Id}"));
        }
        finally
        {
            fresh.SpawnManager = null;
            SwapSingleton(oldPermissions);
            SwapSingleton(oldRequirements);
            SwapSingleton(oldGrant);
            SwapSingleton(oldModels);
            SwapSingleton(oldItems);
        }
    }

    private static WorldInstance HousingPlacementWorld(uint instanceId = 1)
    {
        var regionCount = WorldManager.SECTORS_PER_CELL;
        var zones = new uint[regionCount, regionCount];
        for (var x = 0; x < regionCount; x++)
            for (var y = 0; y < regionCount; y++)
                zones[x, y] = 10;
        var world = new WorldInstance(new WorldTemplate { Id = 0, Name = "placement", CellX = 1, CellY = 1,
            Cells = new WorldCell[1, 1], OceanLevel = -100, ZoneKeyByRegions = zones }, 0, true, instanceId);
        world.Water.OceanLevel = -100;
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(WorldManager.Instance)!;
        worlds[world.Id] = world;
        return world;
    }

    private static void SetParentWorld(GameObject gameObject, WorldInstance world) =>
        gameObject.ParentWorld = world;
}
