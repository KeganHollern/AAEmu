using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class HousingSalePersistenceTests
{
    private static int _nextId = 980000;

    [Fact]
    public void HouseWrite_RollbackKeepsDirtyState_AndCommitAcknowledgesOnlyCapturedValues()
    {
        var house = House((uint)Interlocked.Increment(ref _nextId));
        using var connection = MySQL.CreateConnection();
        lock (SaveManager.PersistenceSyncRoot)
        {
            using (var transaction = connection.BeginTransaction())
            {
                var context = new PersistenceSaveContext(connection, transaction);
                Assert.True(house.Save(context));
                Assert.True(house.IsDirty);
                transaction.Rollback();
            }
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM housings WHERE id={house.Id}"));
            Assert.True(house.IsDirty);

            using (var transaction = connection.BeginTransaction())
            {
                var context = new PersistenceSaveContext(connection, transaction);
                Assert.True(house.Save(context));
                transaction.Commit();
                house.Name = "Changed after writing";
                context.AcknowledgeCommit();
            }
            Assert.True(house.IsDirty);
            using (var transaction = connection.BeginTransaction())
            {
                var context = new PersistenceSaveContext(connection, transaction);
                Assert.True(house.Save(context));
                transaction.Commit();
                context.AcknowledgeCommit();
            }
            Assert.False(house.IsDirty);
            Assert.Equal(house.OwnerId, Scalar($"SELECT owner FROM housings WHERE id={house.Id}"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundCofferSettlement_UsesOneTransactionForHouseItemsDoodadAndContainer(bool failDeletion)
    {
        using var graph = new FurnitureGraph((uint)Interlocked.Increment(ref _nextId));
        var trigger = $"housing_fail_{graph.House.Id}";
        if (failDeletion)
            Execute($"CREATE TRIGGER {trigger} BEFORE DELETE ON item_containers FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected coffer deletion failure'");
        try
        {
            lock (SaveManager.PersistenceSyncRoot)
            {
                var before = HousingSaleState.Capture(graph.House);
                using var inventory = new InventoryMutation(ItemTaskType.BuyHouse);
                using var furniture = new HousingFurnitureSettlement(graph.House, graph.Items);
                Assert.True(furniture.TryPrepare(graph.Buyer, inventory));
                Assert.Same(graph.Backing, furniture.ReturnedItems[graph.Owner.Id][1]);
                graph.House.OwnerId = graph.Buyer.Id;
                graph.House.AccountId = graph.Buyer.AccountId;
                graph.House.SellPrice = 0;
                graph.House.IsDirty = true;
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                var context = new PersistenceSaveContext(connection, transaction);
                graph.Items.Save(context);
                Assert.True(graph.House.Save(context));
                if (failDeletion)
                {
                    Assert.ThrowsAny<Exception>(() => furniture.Save(context));
                    transaction.Rollback();
                    inventory.Dispose();
                    furniture.Dispose();
                    before.Apply(graph.House);
                }
                else
                {
                    furniture.Save(context);
                    transaction.Commit();
                    context.AcknowledgeCommit();
                    inventory.PreservePreparedState();
                    furniture.PreservePreparedState();
                }
            }
        }
        finally
        {
            if (failDeletion)
                Execute($"DROP TRIGGER {trigger}");
        }

        Assert.Equal(failDeletion ? graph.Owner.Id : graph.Buyer.Id,
            Scalar($"SELECT owner FROM housings WHERE id={graph.House.Id}"));
        Assert.Equal(failDeletion ? 1 : 0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={graph.Coffer.DbId}"));
        Assert.Equal(failDeletion ? 1 : 0,
            Scalar($"SELECT COUNT(*) FROM item_containers WHERE container_id={graph.CofferContainerId}"));
        Assert.Equal(graph.Owner.Id, Scalar($"SELECT owner FROM items WHERE id={graph.Backing.Id}"));
        Assert.Equal((long)(failDeletion ? SlotType.System : SlotType.Mail),
            Scalar($"SELECT slot_type FROM items WHERE id={graph.Backing.Id}"));
        Assert.Equal((long)(failDeletion ? SlotType.Trade : SlotType.Mail),
            Scalar($"SELECT slot_type FROM items WHERE id={graph.Content.Id}"));
        Assert.Equal(failDeletion, graph.Containers.ContainsKey(graph.CofferContainerId));
        Assert.Equal(failDeletion ? graph.CofferContainerId : 0UL, graph.Coffer.ItemContainer.ContainerId);
        Assert.Equal(failDeletion ? graph.Owner.Id : graph.Buyer.Id, graph.House.OwnerId);
    }

    [Fact]
    public void FurnitureTransfer_PersistsBuyerOwnerAndDetachesPlantsAtTheirWorldPosition()
    {
        var house = House((uint)Interlocked.Increment(ref _nextId));
        var buyer = Character(house.Id + 10);
        var door = Doodad(house.Id + 20, house, AttachPointKind.Driver);
        var plant = Doodad(house.Id + 21, house, AttachPointKind.None);
        house.Transform.Local.Position = new(100, 200, 300);
        plant.Transform.Parent = house.Transform;
        plant.Transform.Local.Position = new(1, 2, 3);
        var position = plant.Transform.World.Position;
        house.AttachedDoodads.AddRange([door, plant]);
        door.Save();
        plant.Save();
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var inventory = new InventoryMutation(ItemTaskType.BuyHouse);
            using var furniture = new HousingFurnitureSettlement(house, Mock.Of<IItemManager>());
            Assert.True(furniture.TryPrepare(buyer, inventory));
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            var context = new PersistenceSaveContext(connection, transaction);
            furniture.Save(context);
            transaction.Commit();
            context.AcknowledgeCommit();
            inventory.PreservePreparedState();
            furniture.PreservePreparedState();
        }
        Assert.Equal(buyer.Id, Scalar($"SELECT owner_id FROM doodads WHERE id={door.DbId}"));
        Assert.Equal(house.OwnerId, Scalar($"SELECT owner_id FROM doodads WHERE id={plant.DbId}"));
        Assert.Equal(0, Scalar($"SELECT house_id FROM doodads WHERE id={plant.DbId}"));
        Assert.Equal((long)DoodadOwnerType.Character, Scalar($"SELECT owner_type FROM doodads WHERE id={plant.DbId}"));
        Assert.Equal((long)position.X, Scalar($"SELECT x FROM doodads WHERE id={plant.DbId}"));
        Assert.Equal((long)position.Y, Scalar($"SELECT y FROM doodads WHERE id={plant.DbId}"));
        Assert.Equal((long)position.Z, Scalar($"SELECT z FROM doodads WHERE id={plant.DbId}"));
    }

    private sealed class FurnitureGraph : IDisposable
    {
        private readonly object _oldItems, _oldHousingData;
        public ItemManager Items { get; }
        public Dictionary<ulong, ItemContainer> Containers { get; } = [];
        public House House { get; }
        public Character Owner { get; }
        public Character Buyer { get; }
        public DoodadCoffer Coffer { get; }
        public Item Backing { get; }
        public Item Content { get; }
        public ulong CofferContainerId { get; }

        public FurnitureGraph(uint id)
        {
            House = HousingSalePersistenceTests.House(id);
            Owner = Character(House.OwnerId);
            Buyer = Character(id + 10);
            Items = new ItemManager(Mock.Of<ISkillManager>(), Mock.Of<IItemIdManager>(), Mock.Of<IContainerIdManager>(),
                Mock.Of<ILocalizationManager>(), Mock.Of<ITaskManager>(), Mock.Of<IWorldManager>());
            _oldItems = ReplaceSingleton(Items);
            _oldHousingData = ReplaceSingleton(new HousingGameData());
            var system = new ItemContainer(Owner.Id, SlotType.System, false, Owner)
                { ContainerId = id * 10UL + 1, Owner = Owner };
            var mail = new ItemContainer(Owner.Id, SlotType.Mail, false, Owner)
                { ContainerId = id * 10UL + 2, Owner = Owner };
            var cofferContainer = new CofferContainer(Owner.Id, false)
                { ContainerId = id * 10UL + 3, Owner = Owner, ContainerSize = 10 };
            CofferContainerId = cofferContainer.ContainerId;
            foreach (var container in new ItemContainer[] { system, mail, cofferContainer })
                Containers.Add(container.ContainerId, container);
            var template = new ItemTemplate { Id = id, MaxCount = 100, BindType = ItemBindType.Normal };
            Backing = new Item(id * 10UL + 4, template, 1)
                { OwnerId = Owner.Id, SlotType = SlotType.System, Slot = 0, ItemFlags = ItemFlag.SoulBound, _holdingContainer = system };
            Content = new Item(id * 10UL + 5, template, 2)
                { OwnerId = Owner.Id, SlotType = SlotType.Trade, Slot = 0, _holdingContainer = cofferContainer };
            system.Items.Add(Backing);
            cofferContainer.Items.Add(Content);
            system.UpdateFreeSlotCount();
            cofferContainer.UpdateFreeSlotCount();
            SetField(Items, "_allItems", new Dictionary<ulong, Item> { [Backing.Id] = Backing, [Content.Id] = Content });
            SetField(Items, "_allPersistentContainers", Containers);
            SetField(Items, "_removedItems", new List<ulong>());
            SetField(Items, "_templates", new Dictionary<uint, ItemTemplate> { [id] = template });
            SetField(HousingGameData.Instance, "_housingDecorations", new Dictionary<uint, HousingDecoration>
                { [id] = new() { Id = id, DoodadId = id } });
            SetField(HousingGameData.Instance, "_housingItemHousingDecorations", new List<ItemHousingDecoration>
                { new() { DesignId = id, ItemId = id } });
            var date = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
            Coffer = new DoodadCoffer { DbId = id + 20, OwnerId = Owner.Id, OwnerDbId = House.Id,
                OwnerType = DoodadOwnerType.Housing, TemplateId = id, AttachPoint = AttachPointKind.None,
                ItemId = Backing.Id, ItemContainer = cofferContainer, Capacity = 10, IsPersistent = true,
                PlantTime = date, GrowthTime = date, PhaseTime = date };
            House.AttachedDoodads.Add(Coffer);
            Coffer.Save();
            lock (SaveManager.PersistenceSyncRoot)
            {
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                var context = new PersistenceSaveContext(connection, transaction);
                Items.Save(context);
                House.Save(context);
                transaction.Commit();
                context.AcknowledgeCommit();
            }
        }

        public void Dispose()
        {
            SingletonField<ItemManager>().SetValue(null, _oldItems);
            SingletonField<HousingGameData>().SetValue(null, _oldHousingData);
        }
    }

    private static House House(uint id) => new()
    {
        Id = id, TlId = 7, AccountId = id + 1, OwnerId = id + 1, CoOwnerId = id + 1,
        TemplateId = 1, Name = "Settlement test", CurrentStep = -1,
        Template = new HousingTemplate { HousingBindingDoodad = [] },
        PlaceDate = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
        ProtectionEndDate = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
        Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance }, SellPrice = 100, IsDirty = true
    };

    private static Character Character(uint id) => new(new UnitCustomModelParams())
        { Id = id, AccountId = id, Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance } };

    private static Doodad Doodad(uint id, House house, AttachPointKind attach) => new()
    {
        DbId = id, OwnerId = house.OwnerId, OwnerDbId = house.Id, OwnerType = DoodadOwnerType.Housing,
        TemplateId = id, AttachPoint = attach, IsPersistent = true,
        PlantTime = house.PlaceDate, GrowthTime = house.PlaceDate, PhaseTime = house.PlaceDate
    };

    private static long Scalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static FieldInfo SingletonField<T>() => typeof(Singleton<>).MakeGenericType(typeof(T))
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object ReplaceSingleton<T>(T instance)
    {
        var field = SingletonField<T>();
        var previous = field.GetValue(null);
        field.SetValue(null, instance);
        return previous;
    }
    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
