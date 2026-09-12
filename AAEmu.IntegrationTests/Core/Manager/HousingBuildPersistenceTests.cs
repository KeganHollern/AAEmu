using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.CommonFarm;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "player")]
    [InlineData(false, "npc")]
    [InlineData(false, "world_doodad")]
    [InlineData(false, "bound_doodad")]
    [InlineData(false, "crop")]
    public void HouseBuild_CheckpointsPaymentAndCrops_OrRestoresRejectedPlacement(bool certificates, string collision)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var housingData = new HousingGameData();
        var areas = new HousingAreaGameData();
        var farms = new CommonFarmGameData();
        var zones = new ZoneManager(null, null);
        SetField(zones, "_zones", new Dictionary<uint, Zone> { [10] = new() { ZoneKey = 10, GroupId = 50 } });
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
        var oldHousingData = SwapSingleton(housingData);
        var oldAreas = SwapSingleton(areas);
        var oldFarms = SwapSingleton(farms);
        var oldZones = SwapSingleton(zones);
        var actor = new ActorModel { Id = 2 };
        foreach (var stance in Enum.GetValues<GameStanceType>())
            actor.Stances[stance] = new GameStance { HeightCollider = 1, Size = new Vector3(0.5f), UseCapsule = true };
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>
            { [2] = new() { Id = 2, SubId = 2, SubType = "ActorModel" } });
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
            { ["ActorModel"] = new() { [2] = actor } });
        var oldModels = SwapSingleton(models);
        var oldWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, ExpRate = 1 };
        var featuresProperty = typeof(FeaturesManager).GetProperty(nameof(FeaturesManager.Fsets))!;
        var oldFeatures = featuresProperty.GetValue(null);
        var features = new FeatureSet();
        features.Set(Feature.taxItem, certificates);
        featuresProperty.SetValue(null, features);
        try
        {
            var player = graph.Sender;
            player.ObjId = player.Id;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            var design = graph.AddItem(0);
            design.Count = 1;
            var alternativeDesign = graph.AddItem(1);
            var normalCertificates = AddHousingCertificate(graph, Item.TaxCertificate, 2);
            var boundCertificates = AddHousingCertificate(graph, Item.BoundTaxCertificate, 3);
            Assert.True(graph.Save.TryCommitEconomy([player]));
            var houseTemplate = new HousingTemplate
            {
                Id = 100, MainModelId = 1, CategoryId = 16, GardenRadius = 4, HousingBindingDoodad = [],
                Taxation = new Taxation { Tax = 100 },
                BuildSteps = { [0] = new HousingBuildStep { HousingId = 100, Step = 0, NumActions = 1, ModelId = 1 } }
            };
            SetField(housingData, "_housingTemplates", new Dictionary<uint, HousingTemplate> { [100] = houseTemplate });
            SetField(housingData, "_housingItemHousings", new List<HousingItemHousings>
                { new() { Design_Id = 100, Item_Id = design.TemplateId } });
            SetField(areas, "_areas", new Dictionary<uint, HousingAreas> { [11] = new() { Id = 11, GroupId = 2 } });
            var areaGroup = new HousingGroup { Id = 2 };
            areaGroup.CategoryLimits[16] = 0;
            SetField(areas, "_groups", new Dictionary<uint, HousingGroup> { [2] = areaGroup });
            SetField(farms, "_doodadGroups", new Dictionary<uint, DoodadGroups>
                { [6] = new() { Id = 6, RemovedByHouse = true } });
            var world = HousingPlacementWorld();
            world.Regions = new Region[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL];
            world.Template.HousingZones = new()
            {
                [10] = [new HousingAreaPolygon { Id = 11,
                    Points = [new(0, 0, 0), new(1000, 0, 0), new(1000, 1000, 0), new(0, 1000, 0)] }]
            };
            SetParentWorld(player, world);
            player.Transform.Local.Position = new Vector3(100, 200, 300);
            player.Transform.ZoneId = 10;
            var crop = AddHousingCrop(world, player.Id + 40, new Vector3(100, 200, 300), 6);
            var outsideCrop = AddHousingCrop(world, player.Id + 41, new Vector3(110, 200, 300), 6);
            var permanent = AddHousingCrop(world, player.Id + 42, new Vector3(100, 200, 300), 7);
            if (collision == "player")
            {
                player.ModelId = 2;
                HousingField<ConcurrentDictionary<uint, BaseUnit>>(world, "_baseUnits")[player.ObjId] = player;
            }
            if (collision == "npc")
            {
                var npc = new Npc { ObjId = player.Id + 43, ModelId = 2, Template = new NpcTemplate { ModelId = 2, Scale = 1 } };
                SetParentWorld(npc, world);
                npc.Transform.Local.Position = player.Transform.World.Position;
                world.AddObject(npc);
            }
            if (collision is "world_doodad" or "bound_doodad")
            {
                permanent.Template.Model = "cgf://housing-test-decoration.cgf";
                permanent.ParentObjId = collision == "bound_doodad" ? player.ObjId : 0;
            }
            if (collision == "crop")
                crop.Template.Model = "cgf://housing-test-decoration.cgf";
            var houseId = player.Id + 70;
            var objects = new Mock<IObjectIdManager>();
            objects.Setup(ids => ids.GetNextId()).Returns(900);
            var houseIds = new Mock<IHousingIdManager>();
            houseIds.Setup(ids => ids.GetNextId()).Returns(houseId);
            var houseTlds = new Mock<IHousingTldManager>();
            houseTlds.Setup(ids => ids.GetNextId()).Returns(7);
            var factions = new Mock<IFactionManager>();
            factions.Setup(manager => manager.GetFaction(It.IsAny<FactionsEnum>()))
                .Returns((FactionsEnum id) => new SystemFaction { Id = id });
            var locales = new Mock<ILocalizationManager>();
            locales.Setup(manager => manager.Get("housings", "name", 100, "")).Returns("Test house");
            var housing = new HousingManager(objects.Object, factions.Object, locales.Object,
                Mock.Of<IWorldManager>(), Mock.Of<ITaskManager>(), Mock.Of<ISkillManager>(), houseIds.Object,
                houseTlds.Object, graph.Items, graph.Mails, Mock.Of<INameManager>(), zones,
                Mock.Of<IDoodadManager>(), Mock.Of<IUccManager>(), new Lazy<ISaveManager>(() => graph.Save));
            ConfigureHousingGeometry(housing, construction: true);
            var oldHousing = SwapSingleton(housing);
            var trigger = $"house_build_fail_{player.Id}";
            // House writes follow crop deletion in this transaction. The trigger fails only after
            // the removed crop disappears, while the untouched crops still exist.
            if (collision != "success")
                Execute($"CREATE TRIGGER {trigger} AFTER INSERT ON housings FOR EACH ROW BEGIN " +
                $"IF NEW.id={houseId} AND NOT EXISTS(SELECT 1 FROM doodads WHERE id={crop.DbId}) " +
                $"AND EXISTS(SELECT 1 FROM doodads WHERE id={outsideCrop.DbId}) " +
                $"AND EXISTS(SELECT 1 FROM doodads WHERE id={permanent.DbId}) " +
                "THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected house failure after crop deletion'; END IF; END");
            try
            {
                var connection = new GameConnection(null) { ActiveChar = player, AccountId = player.AccountId };
                housing.Build(connection, 100, 100, 200, 300, 0.5f, design.Id, 0, 0, false);

                if (collision == "success")
                {
                    var house = Assert.IsType<House>(housing.GetHouseById(houseId));
                    Assert.Same(house, world.GetBaseUnit(house.ObjId));
                    Assert.True(house.IsVisible);
                    Assert.False(house.IsDirty);
                    Assert.Equal(new Vector3(100, 200, 300), house.Transform.World.Position);
                    Assert.Equal(0.5f, house.Transform.World.Rotation.Z);
                    Assert.Equal(0, house.CurrentStep);
                    Assert.Equal(player.Id, house.OwnerId);
                    Assert.Equal(player.AccountId, house.AccountId);
                    Assert.Equal(HousingPermission.Private, house.Permission);
                    Assert.True(house.ProtectionEndDate > DateTime.UtcNow);
                    Assert.Equal(certificates ? 10000L : 9700L, player.Money);
                    Assert.Equal(player.Money, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
                    Assert.Equal(20, player.LaborPower);
                    Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
                    Assert.Null(graph.Items.GetItemByItemId(design.Id));
                    Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={design.Id}"));
                    Assert.Equal(3, player.Inventory.Bag.Items.Count);
                    Assert.Equal(5, alternativeDesign.Count);
                    Assert.Equal(20, normalCertificates.Count);
                    Assert.Equal(certificates ? 19 : 20, boundCertificates.Count);
                    Assert.Null(world.GetDoodad(crop.ObjId));
                    Assert.False(crop.IsPersistent);
                    Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={crop.DbId}"));
                    foreach (var kept in new[] { outsideCrop, permanent })
                    {
                        Assert.Same(kept, world.GetDoodad(kept.ObjId));
                        Assert.True(kept.IsPersistent);
                        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={kept.DbId}"));
                    }
                    // A replay of the consumed source cannot buy another house or consume more tax.
                    housing.Build(connection, 100, 100, 200, 300, 0.5f, design.Id, 0, 0, false);
                    Assert.Same(house, housing.GetHouseById(houseId));
                    Assert.Equal(certificates ? 10000L : 9700L, player.Money);
                    Assert.Equal(certificates ? 19 : 20, boundCertificates.Count);
                    objects.Verify(ids => ids.GetNextId(), Times.Once());
                    houseIds.Verify(ids => ids.GetNextId(), Times.Once());
                    houseTlds.Verify(ids => ids.GetNextId(), Times.Once());
                    objects.Verify(ids => ids.ReleaseId(It.IsAny<uint>()), Times.Never());
                    houseIds.Verify(ids => ids.ReleaseId(It.IsAny<uint>()), Times.Never());
                    houseTlds.Verify(ids => ids.ReleaseId(It.IsAny<uint>()), Times.Never());

                    var itemsAfterRestart = graph.ReloadLifecycle().Items;
                    Assert.Null(itemsAfterRestart.GetItemByItemId(design.Id));
                    Assert.Equal(5, itemsAfterRestart.GetItemByItemId(alternativeDesign.Id).Count);
                    Assert.Equal(20, itemsAfterRestart.GetItemByItemId(normalCertificates.Id).Count);
                    Assert.Equal(certificates ? 19 : 20, itemsAfterRestart.GetItemByItemId(boundCertificates.Id).Count);
                    var expectedPosition = house.Transform.World.Position;
                    var expectedRotation = house.Transform.World.Rotation;
                    var expectedPlaceDate = house.PlaceDate;
                    var expectedProtection = house.ProtectionEndDate;
                    // Supply templates for other houses in the shared test schema, then discard the live house maps.
                    var templates = new Dictionary<uint, HousingTemplate>();
                    using (var database = MySQL.CreateConnection())
                    using (var command = database.CreateCommand())
                    {
                        command.CommandText = "SELECT DISTINCT template_id FROM housings";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            var id = reader.GetUInt32(0);
                            templates[id] = new HousingTemplate
                            {
                                Id = id, HousingBindingDoodad = [], Taxation = new Taxation { Tax = 100 },
                                BuildSteps = { [0] = new HousingBuildStep { HousingId = id, Step = 0, NumActions = 1 } }
                            };
                        }
                    }
                    templates[houseTemplate.Id] = houseTemplate;
                    SetField(housingData, "_housingTemplates", templates);
                    var nextObject = 1000u;
                    var nextTld = 100u;
                    objects.Setup(ids => ids.GetNextId()).Returns(() => ++nextObject);
                    houseTlds.Setup(ids => ids.GetNextId()).Returns(() => ++nextTld);
                    housing.LoadPlayerHousing(world);
                    var restoredHouse = Assert.IsType<House>(housing.GetHouseById(houseId));
                    Assert.NotSame(house, restoredHouse);
                    Assert.Equal(player.Id, restoredHouse.OwnerId);
                    Assert.Equal(player.AccountId, restoredHouse.AccountId);
                    Assert.Equal(100u, restoredHouse.TemplateId);
                    Assert.Equal(0, restoredHouse.CurrentStep);
                    Assert.Equal(HousingPermission.Private, restoredHouse.Permission);
                    Assert.Equal(expectedPosition, restoredHouse.Transform.World.Position);
                    Assert.Equal(expectedRotation, restoredHouse.Transform.World.Rotation);
                    Assert.Equal(expectedPlaceDate.Ticks / TimeSpan.TicksPerSecond,
                        restoredHouse.PlaceDate.Ticks / TimeSpan.TicksPerSecond);
                    Assert.Equal(expectedProtection.Ticks / TimeSpan.TicksPerSecond,
                        restoredHouse.ProtectionEndDate.Ticks / TimeSpan.TicksPerSecond);
                    Assert.False(restoredHouse.IsDirty);
                    return;
                }

                Assert.Null(housing.GetHouseById(houseId));
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM housings WHERE id={houseId}"));
                Assert.Equal(10000, player.Money);
                Assert.Equal(10000, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
                Assert.Equal(20, player.LaborPower);
                Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
                Assert.Equal(4, player.Inventory.Bag.Items.Count);
                foreach (var item in new[] { design, alternativeDesign, normalCertificates, boundCertificates })
                {
                    Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
                    Assert.Same(player.Inventory.Bag, item._holdingContainer);
                    Assert.Equal(SlotType.Inventory, item.SlotType);
                    var expectedCount = item == design ? 1 : item == alternativeDesign ? 5 : 20;
                    Assert.Equal(expectedCount, item.Count);
                    Assert.Equal(expectedCount, Scalar($"SELECT count FROM items WHERE id={item.Id}"));
                    Assert.Equal((long)SlotType.Inventory, Scalar($"SELECT slot_type FROM items WHERE id={item.Id}"));
                }
                foreach (var doodad in new[] { crop, outsideCrop, permanent })
                {
                    Assert.Same(doodad, world.GetDoodad(doodad.ObjId));
                    Assert.True(doodad.IsPersistent);
                    Assert.False((bool)typeof(Doodad).GetField("_deleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(doodad)!);
                    Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={doodad.DbId}"));
                }
                // Rejected geometry must stop before allocation. Accepted geometry reaches the
                // SQL trigger after crop deletion, then restores all state and releases each ID.
                var allocations = collision is "npc" or "world_doodad" ? Times.Never() : Times.Once();
                objects.Verify(ids => ids.GetNextId(), allocations);
                objects.Verify(ids => ids.ReleaseId(900), allocations);
                houseIds.Verify(ids => ids.GetNextId(), allocations);
                houseIds.Verify(ids => ids.ReleaseId(houseId), allocations);
                houseTlds.Verify(ids => ids.GetNextId(), allocations);
                houseTlds.Verify(ids => ids.ReleaseId(7), allocations);
                var restored = graph.ReloadLifecycle().Items;
                Assert.Equal(1, restored.GetItemByItemId(design.Id).Count);
                Assert.Equal(20, restored.GetItemByItemId(normalCertificates.Id).Count);
                Assert.Equal(20, restored.GetItemByItemId(boundCertificates.Id).Count);
            }
            finally
            {
                if (collision != "success")
                    Execute($"DROP TRIGGER {trigger}");
                SwapSingleton(oldHousing);
            }
        }
        finally
        {
            SwapSingleton(oldHousingData);
            SwapSingleton(oldAreas);
            SwapSingleton(oldFarms);
            SwapSingleton(oldZones);
            SwapSingleton(oldModels);
            AppConfiguration.Instance.World = oldWorldConfig;
            featuresProperty.SetValue(null, oldFeatures);
        }
    }

    private static Item AddHousingCertificate(SendGraph graph, uint templateId, int slot)
    {
        var item = new Item(graph.Sender.Id + 10UL + (uint)slot, new ItemTemplate
            { Id = templateId, MaxCount = 100, BindType = ItemBindType.Normal }, 20)
        {
            OwnerId = graph.Sender.Id, SlotType = SlotType.Inventory, Slot = slot,
            CreateTime = DateTime.UtcNow.Date, _holdingContainer = graph.Sender.Inventory.Bag
        };
        graph.Sender.Inventory.Bag.Items.Add(item);
        graph.Sender.Inventory.Bag.UpdateFreeSlotCount();
        ((Dictionary<ulong, Item>)typeof(ItemManager).GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(graph.Items)!).Add(item.Id, item);
        ((Dictionary<uint, ItemTemplate>)typeof(ItemManager).GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(graph.Items)!)[templateId] = item.Template;
        return item;
    }

    private static Doodad AddHousingCrop(WorldInstance world, uint id, Vector3 position, uint group)
    {
        var date = DateTime.UtcNow.Date;
        var crop = new Doodad { DbId = id, ObjId = id, TemplateId = group, Template = new DoodadTemplate { Id = group, GroupId = group },
            IsPersistent = true, PlantTime = date, PhaseTime = date, GrowthTime = date };
        SetParentWorld(crop, world);
        crop.Transform.Local.Position = position;
        crop.Transform.ZoneId = 10;
        ((ConcurrentDictionary<uint, Doodad>)typeof(WorldInstance)
            .GetField("_doodads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!).TryAdd(id, crop);
        crop.Save();
        return crop;
    }
}
