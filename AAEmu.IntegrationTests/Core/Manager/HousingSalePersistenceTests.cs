using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class HousingSalePersistenceTests
{
    private static int _nextId = 980000;
    private static int _nextPurchaseId = 1300000;

    [Theory]
    [InlineData("none")]
    [InlineData("house")]
    [InlineData("returned_coffer")]
    public void Purchase_CheckpointsAllAssets_RetriesSqlFailure_AndReloadsHouseAndFurniture(string failure)
    {
        using var graph = new PurchaseGraph((uint)Interlocked.Add(ref _nextPurchaseId, 100));
        var protectedUntil = graph.House.ProtectionEndDate;
        if (failure != "none")
        {
            var trigger = $"housing_purchase_fail_{graph.House.Id}";
            var condition = failure == "house"
                ? $"BEFORE INSERT ON housings FOR EACH ROW BEGIN IF NEW.id={graph.House.Id}"
                : $"BEFORE DELETE ON item_containers FOR EACH ROW BEGIN IF OLD.container_id={graph.ReturnedContainerId}";
            Execute($"CREATE TRIGGER {trigger} {condition} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected housing purchase failure'; END IF; END");
            try
            {
                Assert.False(graph.Housing.BuyHouse(graph.House.TlId, PurchaseGraph.Price, graph.Buyer));
                Assert.Equal(10000L, graph.Buyer.Money);
                Assert.Equal(10000, Scalar($"SELECT money FROM characters WHERE id={graph.Buyer.Id}"));
                Assert.Equal(graph.Seller.Id, graph.House.OwnerId);
                Assert.Equal(graph.Seller.Id, Scalar($"SELECT owner FROM housings WHERE id={graph.House.Id}"));
                Assert.Equal(PurchaseGraph.Price, Scalar($"SELECT sell_price FROM housings WHERE id={graph.House.Id}"));
                Assert.Same(graph.OldTaxOffer, Assert.Single(graph.Mails._allPlayerMails.Values));
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE sender_name='.houseSold' AND receiver_id={graph.Seller.Id}"));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={graph.OldTaxOffer.Id}"));
                Assert.Same(graph.Guest.Inventory.SystemContainer, graph.RetainedBacking._holdingContainer);
                Assert.Same(graph.Guest.Inventory.SystemContainer, graph.ReturnedBacking._holdingContainer);
                Assert.Same(graph.Retained.ItemContainer, graph.RetainedContent._holdingContainer);
                Assert.Same(graph.Returned.ItemContainer, graph.ReturnedContent._holdingContainer);
                Assert.Equal(graph.Guest.Id, graph.Retained.OwnerId);
                Assert.Equal(graph.Guest.Id, graph.Retained.ItemContainer.OwnerId);
                Assert.Same(graph.Guest, graph.Retained.OpenedBy);
                Assert.Same(graph.Guest, graph.Returned.OpenedBy);
                Assert.True(graph.Returned.IsPersistent);
                Assert.Equal(DateTime.MinValue, graph.Returned.Despawn);
                Assert.Equal(graph.ReturnedBacking.Id, graph.Returned.ItemId);
                Assert.Equal(graph.ReturnedContainerId, graph.Returned.ItemContainer.ContainerId);
                Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM doodads WHERE house_id={graph.House.Id}"));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM item_containers WHERE container_id={graph.ReturnedContainerId}"));
                foreach (var item in graph.FurnitureItems)
                {
                    Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
                    Assert.Equal(graph.Guest.Id, Scalar($"SELECT owner FROM items WHERE id={item.Id}"));
                    Assert.Equal((long)item.SlotType, Scalar($"SELECT slot_type FROM items WHERE id={item.Id}"));
                    Assert.False(item.IsDirty);
                }
            }
            finally
            {
                Execute($"DROP TRIGGER {trigger}");
            }
        }

        Assert.True(graph.Housing.BuyHouse(graph.House.TlId, PurchaseGraph.Price, graph.Buyer));
        Assert.False(graph.Housing.BuyHouse(graph.House.TlId, PurchaseGraph.Price, graph.Buyer));
        Assert.Equal(9750L, graph.Buyer.Money);
        Assert.Equal(9750, Scalar($"SELECT money FROM characters WHERE id={graph.Buyer.Id}"));
        Assert.Equal(graph.Buyer.Id, Scalar($"SELECT owner FROM housings WHERE id={graph.House.Id}"));
        Assert.Equal(graph.Buyer.AccountId, Scalar($"SELECT account_id FROM housings WHERE id={graph.House.Id}"));
        Assert.Equal(0, Scalar($"SELECT sell_price FROM housings WHERE id={graph.House.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={graph.OldTaxOffer.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={graph.Returned.DbId}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM item_containers WHERE container_id={graph.ReturnedContainerId}"));
        Assert.Null(graph.Items.GetItemContainerByDbId(graph.ReturnedContainerId));
        Assert.Equal(0UL, graph.Returned.ItemContainer.ContainerId);
        Assert.Equal(protectedUntil, graph.House.ProtectionEndDate);
        Assert.Equal(graph.Buyer.Id, graph.Retained.ItemContainer.OwnerId);
        Assert.Empty(graph.Retained.ItemContainer.Items);
        Assert.Null(graph.Retained.OpenedBy);
        Assert.Same(graph.Buyer.Inventory.SystemContainer, graph.RetainedBacking._holdingContainer);
        Assert.Equal(graph.Guest.Id, Scalar($"SELECT owner FROM items WHERE id={graph.ReturnedBacking.Id}"));
        Assert.Equal((long)SlotType.Mail, Scalar($"SELECT slot_type FROM items WHERE id={graph.ReturnedBacking.Id}"));
        graph.AssertSaleMails(graph.Mails, graph.Items);

        // Exercise the production startup readers after discarding every manager and item instance.
        var restored = graph.Reload();
        Assert.NotSame(graph.House, restored.House);
        Assert.Equal(graph.Buyer.Id, restored.House.OwnerId);
        Assert.Equal(graph.Buyer.AccountId, restored.House.AccountId);
        Assert.Equal(HousingPermission.Private, restored.House.Permission);
        Assert.Equal(0u, restored.House.SellPrice);
        Assert.Equal(0u, restored.House.SellToPlayerId);
        Assert.Equal(protectedUntil, restored.House.ProtectionEndDate);
        Assert.Equal(9750, Scalar($"SELECT money FROM characters WHERE id={graph.Buyer.Id}"));
        graph.AssertSaleMails(restored.Mails, restored.Items);
        var coffer = Assert.IsType<DoodadCoffer>(Assert.Single(restored.Furniture));
        Assert.NotSame(graph.Retained, coffer);
        Assert.Equal(graph.Retained.DbId, coffer.DbId);
        Assert.Equal(graph.Buyer.Id, coffer.OwnerId);
        Assert.Equal(graph.Buyer.Id, coffer.ItemContainer.OwnerId);
        Assert.Equal(graph.Retained.ItemContainer.ContainerId, coffer.ItemContainer.ContainerId);
        Assert.Empty(coffer.ItemContainer.Items);
        Assert.Null(coffer.OpenedBy);
        Assert.Same(restored.House.Transform, coffer.Transform.Parent);
        Assert.Same(restored.House, coffer.ParentObj);
        Assert.Equal(graph.Retained.Transform.World.Position, coffer.Transform.World.Position);
        var backing = restored.Items.GetItemByItemId(coffer.ItemId);
        Assert.NotSame(graph.RetainedBacking, backing);
        Assert.Equal(graph.RetainedBacking.Id, backing.Id);
        Assert.Equal(graph.Buyer.Id, backing.OwnerId);
        Assert.Equal(SlotType.System, backing.SlotType);
        Assert.Equal(graph.RetainedBacking.Grade, backing.Grade);
        Assert.Equal(graph.RetainedBacking.UccId, backing.UccId);
        Assert.Equal(graph.Buyer.Id, backing._holdingContainer.OwnerId);
        Assert.Null(restored.Items.GetItemContainerByDbId(graph.ReturnedContainerId));
        Assert.False(restored.Housing.BuyHouse(restored.House.TlId, PurchaseGraph.Price, graph.Buyer));
        Assert.Equal(9750, Scalar($"SELECT money FROM characters WHERE id={graph.Buyer.Id}"));
        graph.AssertSaleMails(restored.Mails, restored.Items);
    }

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

    private sealed class PurchaseGraph : IDisposable
    {
        public const uint Price = 250;
        private readonly Dictionary<FieldInfo, object> _oldSingletons = [];
        private readonly ITaskManager _tasks = Mock.Of<ITaskManager>();
        private readonly Mock<IWorldManager> _world = new();
        private readonly NameManager _names = new();
        private readonly Mock<IMailIdManager> _mailIds = new();
        private readonly Dictionary<uint, ItemTemplate> _templates = [];
        private readonly HousingTemplate _houseTemplate;
        private readonly WorldConfig _oldWorldConfig;
        private uint _nextObjectId;
        private uint _nextMailId;
        private uint _nextHouseTl;
        private ulong _nextContainerId;
        public House House { get; }
        public Character Seller { get; }
        public Character Buyer { get; }
        public Character Guest { get; }
        public ItemManager Items { get; }
        public MailManager Mails { get; }
        public HousingManager Housing { get; }
        public SaveManager Save { get; }
        public DoodadCoffer Retained { get; }
        public DoodadCoffer Returned { get; }
        public Item RetainedBacking { get; }
        public Item ReturnedBacking { get; }
        public Item RetainedContent { get; }
        public Item ReturnedContent { get; }
        public Item[] FurnitureItems => [RetainedBacking, ReturnedBacking, RetainedContent, ReturnedContent];
        public ulong ReturnedContainerId { get; }
        public BaseMail OldTaxOffer { get; }

        public PurchaseGraph(uint id)
        {
            _oldWorldConfig = AppConfiguration.Instance.World;
            AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 7 };
            _nextObjectId = id + 1000;
            _nextMailId = id + 60;
            _nextContainerId = id * 100UL;
            _nextHouseTl = 100;
            _world.Setup(manager => manager.GetAllCharacters()).Returns([]);
            _mailIds.Setup(manager => manager.GetNextId()).Returns(() => ++_nextMailId);
            Replace(new QuestManager(_tasks, Mock.Of<IZoneManager>()));
            Replace(_names);
            _names.Load([], [], []);
            var world = NewWorld();
            Items = NewItems();
            SetField(Items, "_templates", _templates);
            SetField(Items, "_allItems", new Dictionary<ulong, Item>());
            SetField(Items, "_allPersistentContainers", new Dictionary<ulong, ItemContainer>());
            SetField(Items, "_removedItems", new List<ulong>());
            Seller = NewCharacter(id + 1);
            Buyer = NewCharacter(id + 2);
            Guest = NewCharacter(id + 3);
            _houseTemplate = HouseTemplate(id);
            var data = Replace(new HousingGameData());
            SetField(data, "_housingTemplates", new Dictionary<uint, HousingTemplate> { [id] = _houseTemplate });
            House = HousingSalePersistenceTests.House(id);
            House.ObjId = ++_nextObjectId;
            House.Template = _houseTemplate;
            House.TemplateId = id;
            House.ParentWorld = world;
            House.Transform.Local.Position = new(100, 200, 300);
            House.PlaceDate = DateTime.UtcNow.Date;
            House.ProtectionEndDate = House.PlaceDate.AddDays(14);
            House.SellPrice = Price;
            House.SellToPlayerId = Buyer.Id;
            House.Permission = HousingPermission.Public;
            House.AccountId = Seller.AccountId;
            House.OwnerId = Seller.Id;
            RetainedBacking = AddItem(Guest.Inventory.SystemContainer, id + 10, id + 20, 1);
            RetainedBacking.Grade = 4;
            RetainedBacking.UccId = 917;
            ReturnedBacking = AddItem(Guest.Inventory.SystemContainer, id + 11, id + 21, 1);
            ReturnedBacking.ItemFlags = ItemFlag.SoulBound;
            Retained = NewCoffer(world, id + 50, RetainedBacking);
            Returned = NewCoffer(world, id + 51, ReturnedBacking);
            ReturnedContainerId = Returned.ItemContainer.ContainerId;
            RetainedContent = AddItem(Retained.ItemContainer, id + 12, id + 22, 4);
            ReturnedContent = AddItem(Returned.ItemContainer, id + 12, id + 23, 2);
            House.AttachedDoodads.AddRange([Retained, Returned]);
            SetField(data, "_housingDecorations", new Dictionary<uint, HousingDecoration>
            {
                [id + 10] = new() { Id = id + 10, DoodadId = Retained.TemplateId },
                [id + 11] = new() { Id = id + 11, DoodadId = Returned.TemplateId }
            });
            SetField(data, "_housingItemHousingDecorations", new List<ItemHousingDecoration>
            {
                new() { DesignId = id + 10, ItemId = RetainedBacking.TemplateId },
                new() { DesignId = id + 11, ItemId = ReturnedBacking.TemplateId }
            });
            Mails = NewMails(Items);
            Housing = NewHousing(Items, Mails);
            SetField(Housing, "_houses", new Dictionary<uint, House> { [id] = House });
            SetField(Housing, "_housesTl", new Dictionary<ushort, House> { [House.TlId] = House });
            Save = NewSave(Items, Mails, Housing);
            OldTaxOffer = new MailForTax(House) { Id = ++_nextMailId, ReceiverName = Seller.Name,
                Title = "Old tax offer", Header = { ReceiverId = Seller.Id, Extra = House.Id },
                Body = { Text = "Old owner offer", BillingAmount = 5000 } };
            Mails._allPlayerMails.TryAdd(OldTaxOffer.Id, OldTaxOffer);
            Retained.Save();
            Returned.Save();
            Assert.True(Save.TryCommitEconomy([Seller, Buyer, Guest], context => Assert.True(House.Save(context))));
        }

        public void AssertSaleMails(MailManager mails, ItemManager items)
        {
            var ownMails = mails._allPlayerMails.Values.Where(mail =>
                mail.Header.ReceiverId == Buyer.Id || mail.Header.ReceiverId == Seller.Id ||
                mail.Header.ReceiverId == Guest.Id).ToArray();
            Assert.Equal(3, ownMails.Length);
            var profit = Assert.Single(ownMails, mail => mail.Header.SenderName == ".houseSold");
            Assert.Equal(Seller.Id, profit.Header.ReceiverId);
            Assert.Equal((int)Price, profit.Body.CopperCoins);
            Assert.Empty(profit.Body.Attachments);
            Assert.Equal(Buyer.Id, Assert.Single(ownMails, mail => mail.Header.SenderName == ".houseBought").Header.ReceiverId);
            var returned = Assert.Single(ownMails, mail => mail.Header.SenderName == ".houseDemolish");
            Assert.Equal(Guest.Id, returned.Header.ReceiverId);
            Assert.Equal(new[] { RetainedContent.Id, ReturnedBacking.Id, ReturnedContent.Id }.Order(),
                returned.Body.Attachments.Select(item => item.Id).Order());
            foreach (var original in new[] { RetainedContent, ReturnedBacking, ReturnedContent })
            {
                var attached = returned.Body.Attachments.Single(item => item.Id == original.Id);
                Assert.Same(items.GetItemByItemId(original.Id), attached);
                Assert.Equal(Guest.Id, attached.OwnerId);
                Assert.Equal(Guest.Id, attached._holdingContainer.OwnerId);
                Assert.Equal(SlotType.Mail, attached.SlotType);
                Assert.Equal(original.Count, attached.Count);
                Assert.Equal(original.ItemFlags, attached.ItemFlags);
            }
            Assert.DoesNotContain(ownMails, MailForTax.IsTaxMail);
        }

        public (House House, HousingManager Housing, ItemManager Items, MailManager Mails, List<Doodad> Furniture) Reload()
        {
            var world = NewWorld();
            var items = NewItems();
            var templates = new Dictionary<uint, ItemTemplate>(_templates);
            var houses = new Dictionary<uint, HousingTemplate>();
            using (var connection = MySQL.CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT template_id, type FROM items";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var id = reader.GetUInt32("template_id");
                        if (templates.ContainsKey(id)) continue;
                        var type = typeof(Item).Assembly.GetType(reader.GetString("type"));
                        ItemTemplate template = type != null && typeof(EquipItem).IsAssignableFrom(type)
                            ? new EquipItemTemplate() : new ItemTemplate();
                        template.Id = id;
                        template.MaxCount = int.MaxValue;
                        template.FixedGrade = -1;
                        template.Gradable = true;
                        templates[id] = template;
                    }
                command.CommandText = "SELECT DISTINCT template_id FROM housings";
                using var housingRows = command.ExecuteReader();
                while (housingRows.Read())
                    houses[housingRows.GetUInt32(0)] = HouseTemplate(housingRows.GetUInt32(0));
            }
            houses[House.TemplateId] = _houseTemplate;
            SetField(HousingGameData.Instance, "_housingTemplates", houses);
            SetField(items, "_templates", templates);
            items.LoadUserItems();
            var mails = NewMails(items);
            mails.Load();
            var housing = NewHousing(items, mails);
            housing.LoadPlayerHousing(world);
            var house = housing.GetHouseById(House.Id);
            world.SpawnManager.SpawnPersistentDoodads(DoodadOwnerType.Housing, (int)House.Id);
            var furniture = (List<Doodad>)typeof(SpawnManager)
                .GetProperty("PlayerDoodads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world.SpawnManager)!;
            return (house, housing, items, mails, furniture);
        }

        private WorldInstance NewWorld()
        {
            var manager = Replace(new WorldManager(Mock.Of<ITickManager>(), Mock.Of<IWorldIdManager>(),
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>()),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>()),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>())));
            var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "housing_test" }, 0, true, 1);
            ((ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!).TryAdd(1, world);
            world.SpawnManager = new SpawnManager(world);
            return world;
        }

        private ItemManager NewItems() => Replace(new ItemManager(Mock.Of<ISkillManager>(),
            Mock.Of<IItemIdManager>(), Mock.Of<IContainerIdManager>(), Mock.Of<ILocalizationManager>(), _tasks, _world.Object));

        private MailManager NewMails(ItemManager items) => new(_mailIds.Object, _names, items, _tasks,
            _world.Object, new Lazy<IHousingManager>(() => HousingManager.Instance), Mock.Of<ILocalizationManager>())
            { _allPlayerMails = [] };

        private HousingManager NewHousing(ItemManager items, MailManager mails)
        {
            var objectIds = new Mock<IObjectIdManager>();
            objectIds.Setup(manager => manager.GetNextId()).Returns(() => ++_nextObjectId);
            var houseTlds = new Mock<IHousingTldManager>();
            houseTlds.Setup(manager => manager.GetNextId()).Returns(() => ++_nextHouseTl);
            var factions = new Mock<IFactionManager>();
            factions.Setup(manager => manager.GetFaction(It.IsAny<FactionsEnum>()))
                .Returns((FactionsEnum id) => new SystemFaction { Id = id });
            var zones = new Mock<IZoneManager>();
            zones.Setup(manager => manager.GetZoneByKey(It.IsAny<uint>())).Returns(new Zone { GroupId = 9 });
            var doodads = Replace(new DoodadManager(objectIds.Object, Mock.Of<IDoodadIdManager>(), items,
                new Lazy<IHousingManager>(() => HousingManager.Instance), Mock.Of<ISusManager>()));
            SetField(doodads, "_templates", new Dictionary<uint, DoodadTemplate>
            {
                [Retained.TemplateId] = Retained.Template,
                [Returned.TemplateId] = Returned.Template
            });
            HousingManager housing = null;
            housing = new HousingManager(objectIds.Object, factions.Object, Mock.Of<ILocalizationManager>(),
                _world.Object, _tasks, Mock.Of<ISkillManager>(), Mock.Of<IHousingIdManager>(), houseTlds.Object,
                items, mails, _names, zones.Object, doodads, Mock.Of<IUccManager>(),
                new Lazy<ISaveManager>(() => NewSave(items, mails, housing)));
            return Replace(housing);
        }

        private SaveManager NewSave(ItemManager items, MailManager mails, HousingManager housing) =>
            new(_tasks, housing, mails, items, Mock.Of<IAuctionManager>(), Mock.Of<ICrimeManager>(), _world.Object, Mock.Of<IZoneManager>());

        private Character NewCharacter(uint id)
        {
            var character = new Character(new UnitCustomModelParams())
            {
                Id = id, ObjId = ++_nextObjectId, AccountId = id + 100,
                Name = $"Housing{id}", Money = 10000, NumInventorySlots = 10, NumBankSlots = 10,
                Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance }, FactionName = "",
                Slots = [], Created = DateTime.UtcNow.Date
            };
            _names.AddCharacter(id, character.Name, character.AccountId);
            foreach (var type in Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate))
                Register(new ItemContainer(id, type, false, character) { Owner = character });
            character.Inventory = new Inventory(character);
            character.Mails = new CharacterMails(character);
            return character;
        }

        private T Register<T>(T container) where T : ItemContainer
        {
            container.ContainerId = ++_nextContainerId;
            ((Dictionary<ulong, ItemContainer>)typeof(ItemManager)
                .GetField("_allPersistentContainers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Items)!)
                .Add(container.ContainerId, container);
            return container;
        }

        private Item AddItem(ItemContainer container, uint templateId, ulong id, int count)
        {
            if (!_templates.TryGetValue(templateId, out var template))
                _templates[templateId] = template = new ItemTemplate
                    { Id = templateId, MaxCount = 100, FixedGrade = -1, Gradable = true, BindType = ItemBindType.Normal };
            var item = new Item(id, template, count) { OwnerId = container.OwnerId, SlotType = container.ContainerType,
                Slot = container.Items.Count, CreateTime = DateTime.UtcNow.Date, _holdingContainer = container };
            container.Items.Add(item);
            container.UpdateFreeSlotCount();
            ((Dictionary<ulong, Item>)typeof(ItemManager).GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Items)!).Add(id, item);
            return item;
        }

        private DoodadCoffer NewCoffer(WorldInstance world, uint id, Item backing)
        {
            var coffer = new DoodadCoffer { DbId = id, ObjId = ++_nextObjectId, TemplateId = backing.TemplateId,
                Template = new DoodadCofferTemplate { Id = backing.TemplateId, Capacity = 10 },
                ParentWorld = world, ParentObj = House, ParentObjId = House.ObjId,
                OwnerId = Guest.Id, OwnerDbId = House.Id, OwnerType = DoodadOwnerType.Housing,
                AttachPoint = AttachPointKind.None,
                ItemId = backing.Id, ItemTemplateId = backing.TemplateId, IsPersistent = true,
                PlantTime = House.PlaceDate, PhaseTime = House.PlaceDate, GrowthTime = House.PlaceDate,
                Capacity = 10, OpenedBy = Guest,
                ItemContainer = Register(new CofferContainer(Guest.Id, false) { Owner = Guest, ContainerSize = 10 }) };
            coffer.Transform.Parent = House.Transform;
            coffer.Transform.Local.Position = new(2, 3, 4);
            world.AddObject(coffer);
            world.SpawnManager.AddPlayerDoodad(coffer);
            return coffer;
        }

        private static HousingTemplate HouseTemplate(uint id) => new()
            { Id = id, IsSellable = true, HousingBindingDoodad = [], Taxation = new Taxation { Tax = 5000 } };

        private T Replace<T>(T instance) where T : class
        {
            var field = SingletonField<T>();
            _oldSingletons.TryAdd(field, field.GetValue(null));
            field.SetValue(null, instance);
            return instance;
        }

        public void Dispose()
        {
            AppConfiguration.Instance.World = _oldWorldConfig;
            foreach (var (field, previous) in _oldSingletons)
                field.SetValue(null, previous);
        }
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
        TemplateId = 1, Name = "Settlement test",
        Template = new HousingTemplate { HousingBindingDoodad = [] },
        CurrentStep = -1,
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
