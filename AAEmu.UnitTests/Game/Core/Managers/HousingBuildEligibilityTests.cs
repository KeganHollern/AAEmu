using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class HousingBuildEligibilityTests
{
    [Test]
    [Arguments("outside")]
    [Arguments("category")]
    [Arguments("houseless")]
    [Arguments("existing")]
    [Arguments("maximum")]
    [Arguments("design")]
    [Arguments("nan")]
    [Arguments("failed_commit")]
    public async Task Build_RejectedEligibility_PreservesDesignCertificatesAndMoney(string rejection)
    {
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        var housing = new HousingGameData();
        var areas = new HousingAreaGameData();
        var previousItems = Swap(items);
        var previousHousing = Swap(housing);
        var previousAreas = Swap(areas);
        var previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 7 };
        var farms = new CommonFarmGameData();
        var previousFarms = Swap(farms);
        var previousAccounts = Swap(new AccountManager(null, null, TimeProvider.System));
        var fsets = typeof(FeaturesManager).GetProperty(nameof(FeaturesManager.Fsets))!;
        var previousFeatures = fsets.GetValue(null);
        var features = new FeatureSet();
        features.Set(Feature.taxItem, false);
        fsets.SetValue(null, features);
        var worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var previousWorlds = Swap(worlds);
        try
        {
            var saves = Mock.Of<ISaveManager>();
            var ids = Mock.Of<IHousingIdManager>();
            ids.GetNextId().Returns(50u);
            var tldIds = Mock.Of<IHousingTldManager>();
            tldIds.GetNextId().Returns(60u);
            var locales = Mock.Of<ILocalizationManager>();
            locales.Get("housings", "name", 100, "").Returns("Test house");
            var manager = new HousingManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IFactionManager>().Object,
                locales.Object, Mock.Of<IWorldManager>().Object, Mock.Of<ITaskManager>().Object,
                Mock.Of<ISkillManager>().Object, ids.Object, tldIds.Object,
                items, Mock.Of<IMailManager>().Object, Mock.Of<INameManager>().Object, Mock.Of<IZoneManager>().Object,
                Mock.Of<IDoodadManager>().Object, Mock.Of<IUccManager>().Object,
                new Lazy<ISaveManager>(() => saves.Object));
            var template = new HousingTemplate
            {
                Id = 100, MainModelId = 1, CategoryId = 16, GardenRadius = 4, Alley = 1, HousingBindingDoodad = [],
                Taxation = new Taxation { Tax = 100 }
            };
            Field<Dictionary<uint, HousingTemplate>>(housing, "_housingTemplates").Add(100, template);
            Field<List<HousingItemHousings>>(housing, "_housingItemHousings")
                .Add(new HousingItemHousings { Design_Id = 100, Item_Id = rejection == "design" ? 999u : 200u });
            Field<Dictionary<uint, HousingAreas>>(areas, "_areas").Add(11, new HousingAreas { Id = 11, GroupId = 2 });
            var group = new HousingGroup
            {
                Id = 2,
                Houseless = rejection == "houseless",
                ExistingCategoryId = rejection == "existing" ? 17u : 0u
            };
            group.CategoryLimits.Add(rejection == "category" ? 1u : 16u, rejection == "maximum" ? 1u : 0u);
            Field<Dictionary<uint, HousingGroup>>(areas, "_groups").Add(2, group);
            var world = new WorldTemplate
            {
                HousingZones = new()
                {
                    [138] = [new HousingAreaPolygon
                {
                    Id = 11, Points = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)]
                }]
                }
            };
            world.Cells = new WorldCell[1, 1];
            world.OceanLevel = -100;
            manager.GeometryAssets = new HousingGeometryAssets(path => path.EndsWith("heightmap.dat", StringComparison.Ordinal)
                ? FlatTerrain() : null, (_, _) => []);
            Field<ConcurrentDictionary<uint, CryGeometryAsset>>(manager.GeometryAssets, "_models")[1] =
                new CryGeometryAsset(new CryBounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 2)), []);
            var instance = new WorldInstance(world, 0, true, 0);
            instance.Water.OceanLevel = -100;
            Field<ConcurrentDictionary<uint, WorldInstance>>(worlds, "_worlds").TryAdd(0, instance);
            var character = new CharacterMock
            {
                Id = 7,
                AccountId = 42,
                Faction = new SystemFaction(),
                Money = 1000000,
                NumInventorySlots = 10,
                NumBankSlots = 10,
                ParentWorld = instance,
                Connection = new GameConnection(null)
            };
            var containers = new Dictionary<ulong, ItemContainer>();
            foreach (var slotType in Enum.GetValues<SlotType>().Where(slot => slot != SlotType.EquipmentMate))
            {
                var container = new ItemContainer(character.Id, slotType, false, character)
                { ContainerId = (ulong)containers.Count + 1, Owner = character };
                containers.Add(container.ContainerId, container);
            }
            typeof(ItemManager).GetField("_allPersistentContainers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(items, containers);
            character.Inventory = new Inventory(character);
            var design = Add(character, 1, 200, 1);
            var certificates = Add(character, 2, Item.TaxCertificate, 20);
            var boundCertificates = Add(character, 3, Item.BoundTaxCertificate, 20);
            var houses = Field<Dictionary<uint, House>>(manager, "_houses");
            houses.Add(1, new House
            {
                Id = 1,
                OwnerId = 8,
                AccountId = 42,
                Template = new HousingTemplate { CategoryId = rejection == "existing" ? 17u : 16u }
            });
            var crop = new Doodad
            {
                ObjId = 900, Template = new DoodadTemplate { GroupId = 6 }, ParentWorld = instance
            };
            crop.Transform.Local.SetPosition(5, 5, 0);
            Field<ConcurrentDictionary<uint, Doodad>>(instance, "_doodads").TryAdd(crop.ObjId, crop);
            Field<Dictionary<uint, DoodadGroups>>(farms, "_doodadGroups")[6] = new() { Id = 6, RemovedByHouse = true };
            var observations = 0;
            saves.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>())
                .Returns(() =>
                {
                    if (houses.ContainsKey(50) && design.Count == 0 && character.Money == 999700 &&
                        Field<bool>(crop, "_deleted"))
                        observations++;
                    return false;
                });
            var connection = new GameConnection(null) { ActiveChar = character, AccountId = 42 };
            var x = rejection == "outside" ? -1 : rejection == "nan" ? float.NaN : 5;
            manager.Build(connection, 100, x, 5, 0, 0, design.Id, 0, 0, false);
            await Assert.That(character.Money).IsEqualTo(1000000L);
            await Assert.That(design.Count).IsEqualTo(1);
            await Assert.That(certificates.Count).IsEqualTo(20);
            await Assert.That(boundCertificates.Count).IsEqualTo(20);
            await Assert.That(character.Inventory.Bag.Items.Count).IsEqualTo(3);
            await Assert.That(houses.Count).IsEqualTo(1);
            await Assert.That(Field<bool>(crop, "_deleted")).IsFalse();
            await Assert.That(instance.GetDoodad(crop.ObjId)).IsSameReferenceAs(crop);
            await Assert.That(observations).IsEqualTo(rejection == "failed_commit" ? 1 : 0);
        }
        finally
        {
            Swap(previousItems);
            Swap(previousHousing);
            Swap(previousAreas);
            Swap(previousWorlds);
            Swap(previousFarms);
            Swap(previousAccounts);
            fsets.SetValue(null, previousFeatures);
            AppConfiguration.Instance.World = previousWorldConfig;
        }
    }

    private static MemoryStream FlatTerrain()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(24); writer.Write(0); writer.Write(4096); writer.Write(2);
        writer.Write(64); writer.Write(128); writer.Write(0.0625f); writer.Write(100f);
        writer.Write(new byte[128]);
        writer.Write(5);
        foreach (var value in new[] { 0f, 0f, 0f, 1024f, 1024f, 0f }) writer.Write(value);
        writer.Write(false); writer.Write(0f); writer.Write(1f / 32); writer.Write(2); writer.Write(0);
        writer.Write(new byte[8 + 20 + 36]);
        stream.Position = 4;
        writer.Write((int)stream.Length);
        stream.Position = 0;
        return stream;
    }

    private static Item Add(CharacterMock owner, ulong id, uint templateId, int count)
    {
        var item = new Item
        {
            Id = id,
            OwnerId = owner.Id,
            TemplateId = templateId,
            Template = new ItemTemplate { Id = templateId },
            Count = count,
            SlotType = SlotType.Inventory,
            Slot = (int)id,
            _holdingContainer = owner.Inventory.Bag
        };
        owner.Inventory.Bag.Items.Add(item);
        return item;
    }

    private static T Swap<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (T)field.GetValue(null);
        field.SetValue(null, value);
        return previous;
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
}
