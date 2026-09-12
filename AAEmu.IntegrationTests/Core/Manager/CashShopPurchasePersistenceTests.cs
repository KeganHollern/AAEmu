using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.CashShop;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Units;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class CashShopPurchasePersistenceTests
{
    private static int _nextId = 1600000;

    [Fact]
    public void Cart_ReloadsExactMailItemsPaymentStockAndImmutableQuantity()
    {
        using var graph = new PurchaseGraph();
        var manager = Shop(graph);
        Assert.Equal(ErrorMessageType.NoErrorMessage, Buy(graph, manager));
        Assert.Equal(3, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Equal(8, Scalar($"SELECT remaining FROM ics_shop_items WHERE shop_id={graph.Id}"));
        Assert.Equal(2, Scalar($"SELECT item_count FROM audit_ics_sales WHERE buyer_char={graph.Buyer.Id}"));
        var itemId = graph.OwnMails().Single().Body.Attachments.Single().Id;
        using var restored = new PurchaseGraph(graph.Id);
        var mail = restored.OwnMails().Single();
        Assert.Equal(itemId, mail.Body.Attachments.Single().Id);
        Assert.Equal(2, mail.Body.Attachments.Single().Count);
        Assert.Same(restored.Other.Inventory.MailAttachments, mail.Body.Attachments.Single()._holdingContainer);
    }

    [Theory]
    [InlineData("debit")]
    [InlineData("mail")]
    [InlineData("audit")]
    public void Failure_RestoresWholeCartAndAllowsOneCleanRetry(string failure)
    {
        using var graph = new PurchaseGraph();
        var manager = Shop(graph);
        var trigger = $"cash_failure_{graph.Id}";
        if (failure == "debit") Execute($"UPDATE accounts SET credits=0 WHERE account_id={graph.Buyer.AccountId}");
        else Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {(failure == "mail" ? "mails" : "audit_ics_sales")} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected cash purchase failure'");
        try
        {
            Assert.NotEqual(ErrorMessageType.NoErrorMessage, Buy(graph, manager));
            Assert.Empty(graph.OwnMails());
            Assert.Equal(10, manager.ShopItems[graph.Id].Remaining);
            Assert.Equal(10, Scalar($"SELECT remaining FROM ics_shop_items WHERE shop_id={graph.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM audit_ics_sales WHERE buyer_char={graph.Buyer.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE owner={graph.Other.Id}"));
        }
        finally
        {
            if (failure != "debit") Execute($"DROP TRIGGER {trigger}");
        }
        Execute($"UPDATE accounts SET credits=10 WHERE account_id={graph.Buyer.AccountId}");
        Assert.Equal(ErrorMessageType.NoErrorMessage, Buy(graph, manager));
        Assert.Single(graph.OwnMails());
        Assert.Equal(3, ReadAccount(graph.Buyer.AccountId, "credits"));
    }

    [Fact]
    public async Task ConcurrentCarts_OnlyOneCartCanSpendTheLastAccountFunds()
    {
        using var graph = new PurchaseGraph();
        var manager = Shop(graph);
        var carts = await Task.WhenAll(Task.Run(() => Buy(graph, manager)), Task.Run(() => Buy(graph, manager)));
        Assert.Single(carts, result => result == ErrorMessageType.NoErrorMessage);
        Assert.Single(graph.OwnMails());
        Assert.Equal(3, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Equal(8, Scalar($"SELECT remaining FROM ics_shop_items WHERE shop_id={graph.Id}"));
    }

    [Fact]
    public void LegacySaleWithoutQuantity_RejectsLimitedPurchaseWithoutAnyAssetChange()
    {
        using var graph = new PurchaseGraph();
        var manager = Shop(graph);
        manager.ShopItems[graph.Id].LimitedType = CashShopLimitType.Account;
        manager.ShopItems[graph.Id].LimitedStockMax = 10;
        Execute($"INSERT INTO audit_ics_sales (buyer_account,buyer_char,shop_item_id,sku,description) VALUES ({graph.Buyer.AccountId},{graph.Buyer.Id},{graph.Id},42,'legacy')");
        Assert.Equal(ErrorMessageType.IngameShopSoldOut, Buy(graph, manager));
        Assert.Empty(graph.OwnMails());
        Assert.Equal(10, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Equal(10, manager.ShopItems[graph.Id].Remaining);
    }

    [Fact]
    public void QuantityMigration_IsRepeatableAndLeavesLegacyQuantitiesUnknown()
    {
        using var graph = new PurchaseGraph();
        Shop(graph);
        Execute($"INSERT INTO audit_ics_sales (buyer_account,buyer_char,shop_item_id,sku,description) VALUES ({graph.Buyer.AccountId},{graph.Buyer.Id},{graph.Id},42,'legacy')");
        Execute("ALTER TABLE audit_ics_sales DROP COLUMN item_count");
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SQL", "updates", "2026-09-12_aaemu_game_ics_sale_quantity.sql"));
        Execute(sql);
        Execute(sql);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM audit_ics_sales WHERE buyer_char={graph.Buyer.Id} AND item_count IS NULL"));
        Assert.Equal(10, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Equal(10, Scalar($"SELECT remaining FROM ics_shop_items WHERE shop_id={graph.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCommit_StopsAllLaterPersistenceAndReloadsOnlyDurableState(bool committed)
    {
        using var graph = new PurchaseGraph();
        var manager = Shop(graph);
        Assert.True(graph.Save.TryCommitEconomy([graph.Buyer]));
        var stops = 0;
        graph.Save.StopForConsistencyFailure = (_, _) => stops++;
        graph.Save.CommitTransaction = transaction =>
        {
            if (committed) transaction.Commit();
            else transaction.Rollback();
            throw new IOException("Injected lost commit acknowledgement");
        };
        Assert.Throws<IOException>(() => Buy(graph, manager));
        Assert.Equal(1, stops);
        Assert.False(graph.Save.DoSave());
        Assert.Throws<InvalidOperationException>(() => graph.Save.TryCommitEconomy([graph.Buyer]));
        Assert.Equal(committed ? 3 : 10, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Equal(committed ? 1 : 0, Scalar($"SELECT COUNT(*) FROM mails WHERE receiver_id={graph.Other.Id}"));
        using var restored = new PurchaseGraph(graph.Id);
        Assert.Equal(committed ? 1 : 0, restored.OwnMails().Length);
    }

    [Fact]
    public void DeletedRecipient_WithStaleLiveObject_RejectsBeforeCharacterSave()
    {
        using var graph = new PurchaseGraph();
        var manager = Shop(graph);
        Execute($"UPDATE characters SET deleted=1 WHERE id={graph.Other.Id}");
        Assert.Equal(ErrorMessageType.IngameShopFindCharacterNameFail, Buy(graph, manager));
        Assert.Equal(1, Read("characters", "deleted", graph.Other.Id));
        Assert.Equal(10, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Empty(graph.OwnMails());
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE receiver_id={graph.Other.Id}"));
    }

    [Fact]
    public void AccountDebits_AreConditionalAcrossManagerInstances()
    {
        using var graph = new PurchaseGraph();
        Shop(graph);
        var first = Accounts();
        var second = Accounts();
        Assert.True(first.RemoveCredits(graph.Buyer.AccountId, 8));
        Assert.False(second.RemoveCredits(graph.Buyer.AccountId, 8));
        Assert.False(second.RemoveCredits(graph.Buyer.AccountId, -8));
        Assert.False(second.AddLoyalty(graph.Buyer.AccountId, -1));
        Assert.Equal(2, ReadAccount(graph.Buyer.AccountId, "credits"));
        Assert.Equal(0, ReadAccount(graph.Buyer.AccountId, "loyalty"));
    }

    private static AccountManager Accounts() => new(Mock.Of<ITickManager>(), Mock.Of<ITimedRewardsManager>(), TimeProvider.System);
    private static CashShopManager Shop(PurchaseGraph graph)
    {
        Execute($"INSERT INTO accounts(account_id,credits,loyalty) VALUES ({graph.Buyer.AccountId},10,0)");
        Execute($"INSERT INTO ics_shop_items(shop_id,remaining) VALUES ({graph.Id},10)");
        Assert.True(graph.Save.TryCommitEconomy([graph.Buyer]));
        var sku = new IcsSku { Sku = graph.Id + 40, ShopId = graph.Id, ItemId = 100, ItemCount = 2, Price = 10, DiscountPrice = 7 };
        var manager = new CashShopManager(Mock.Of<IWorldManager>(), Accounts(), Mock.Of<ILocalizationManager>())
        {
            CommitPurchase = (participants, write, validate) => graph.Save.TryCommitEconomy(participants, write, validate)
        };
        manager.SKUs[sku.Sku] = sku;
        manager.ShopItems[graph.Id] = new IcsItem { ShopId = graph.Id, Remaining = 10, Name = "Cash item", Skus = { [sku.Sku] = sku } };
        return manager;
    }
    private static ErrorMessageType Buy(PurchaseGraph graph, CashShopManager manager)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var result = manager.TryPlanPurchase(graph.Buyer, true, [graph.Id + 40], DateTime.UtcNow, out var plan);
            return result != ErrorMessageType.NoErrorMessage ? result :
                manager.SettlePurchase(graph.Buyer, graph.Other.Id, graph.Other.AccountId, graph.Other.Name, plan);
        }
    }
    private static long ReadAccount(uint id, string field) => Scalar($"SELECT {field} FROM accounts WHERE account_id={id}");
    private static long Scalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static long Read(string table, string field, ulong id) => Scalar($"SELECT {field} FROM {table} WHERE id={id}");
    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class PurchaseGraph : IDisposable
    {
        private static readonly FieldInfo s_items = typeof(Singleton<ItemManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo s_quests = typeof(Singleton<QuestManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo s_mail = typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previousItems = s_items.GetValue(null);
        private readonly object _previousQuests = s_quests.GetValue(null);
        private readonly object _previousMail = s_mail.GetValue(null);
        public uint Id { get; }
        public ItemManager Items { get; }
        public MailManager Mail { get; }
        public AuctionManager Auction { get; }
        public SaveManager Save { get; }
        public TestCharacter Seller { get; }
        public TestCharacter Buyer { get; }
        public TestCharacter Other { get; }

        public PurchaseGraph(uint? restoreId = null)
        {
            Id = restoreId ?? (uint)Interlocked.Add(ref _nextId, 100);
            Seller = Character(Id + 1);
            Buyer = Character(Id + 2);
            Other = Character(Id + 3);
            var players = new[] { Seller, Buyer, Other };
            var world = new Mock<IWorldManager>();
            world.Setup(manager => manager.GetAllCharacters()).Returns([Seller, Buyer, Other]);
            world.Setup(manager => manager.GetWorlds()).Returns([]);
            var names = new NameManager();
            names.Load([], [], []);
            foreach (var player in players) names.AddCharacter(player.Id, player.Name, player.AccountId);
            var locale = new Mock<ILocalizationManager>();
            locale.Setup(manager => manager.Get("items", "name", 100, "Item:100")).Returns("Auction item");
            var itemIds = new Mock<IItemIdManager>();
            uint itemId = Id * 100 + 50;
            itemIds.Setup(manager => manager.GetNextId()).Returns(() => itemId++);
            Items = new ItemManager(Mock.Of<ISkillManager>(), itemIds.Object,
                Mock.Of<IContainerIdManager>(), locale.Object, Mock.Of<ITaskManager>(), world.Object);
            s_items.SetValue(null, Items);
            s_quests.SetValue(null, new QuestManager(Mock.Of<ITaskManager>(), Mock.Of<IZoneManager>()));
            SetField(Items, "_allItems", new Dictionary<ulong, Item>());
            SetField(Items, "_removedItems", new List<ulong>());
            var templates = new Dictionary<uint, ItemTemplate> { [100] = Template(100) };
            if (restoreId != null)
            {
                // Other GameMySql fixtures share this disposable schema. Their items need templates too.
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT template_id FROM items";
                using var reader = command.ExecuteReader();
                while (reader.Read()) templates.TryAdd(reader.GetUInt32(0), Template(reader.GetUInt32(0)));
            }
            SetField(Items, "_templates", templates);
            var containers = new Dictionary<ulong, ItemContainer>();
            SetField(Items, "_allPersistentContainers", containers);
            if (restoreId != null) Items.LoadUserItems();
            else
            {
                foreach (var player in players)
                foreach (var type in Enum.GetValues<SlotType>())
                {
                    if (type == SlotType.EquipmentMate) continue;
                    var container = new ItemContainer(player.Id, type, false, player)
                        { Owner = player, ContainerId = Id * 100UL + (ulong)containers.Count + 1 };
                    containers.Add(container.ContainerId, container);
                }
            }
            foreach (var player in players)
            {
                player.Inventory = new Inventory(player);
                if (restoreId != null) player.Money = Read("characters", "money", player.Id);
            }
            var mailIds = new Mock<IMailIdManager>();
            uint mailId = Id + 20;
            mailIds.Setup(manager => manager.GetNextId()).Returns(() => mailId++);
            Mail = new MailManager(mailIds.Object, names, Items, Mock.Of<ITaskManager>(), world.Object,
                new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>()), locale.Object) { _allPlayerMails = [] };
            s_mail.SetValue(null, Mail);
            var auctionIds = new Mock<IAuctionIdManager>();
            uint lotId = Id + 10;
            auctionIds.Setup(manager => manager.GetNextId()).Returns(() => lotId++);
            Auction = new AuctionManager(Items, names, auctionIds.Object, locale.Object,
                Mock.Of<ITaskManager>(), Mail, new Lazy<ISaveManager>(() => Save));
            Save = new SaveManager(Mock.Of<ITaskManager>(), Mock.Of<IHousingManager>(), Mail, Items, Auction,
                Mock.Of<ICrimeManager>(), world.Object, Mock.Of<IZoneManager>());
            if (restoreId != null) { Mail.Load(); Auction.Load(); }
        }

        public Item AddItem()
        {
            var template = Items.GetTemplate(100);
            var item = new Item { Id = Id + 4, OwnerId = Seller.Id, TemplateId = 100, Template = template,
                SlotType = SlotType.Inventory, Slot = 3, Count = 3, Grade = 6, UccId = 55,
                CreateTime = DateTime.UtcNow, ExpirationTime = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpirationOnlineMinutesLeft = 29.5, _holdingContainer = Seller.Inventory.Bag };
            Field<Dictionary<ulong, Item>>(Items, "_allItems").Add(item.Id, item);
            Seller.Inventory.Bag.Items.Add(item);
            Seller.Inventory.Bag.UpdateFreeSlotCount();
            return item;
        }

        public void Post(Item item) => Auction.PostLotOnAuction(Seller, 0, 0, item.Id, 100, 2000, AuctionDuration.AuctionDuration6Hours);
        public void Bid(Character player, ulong lotId, int amount) => Auction.BidOnAuctionLot(player, 0, 0,
            new AuctionLot { Id = lotId }, new AuctionBid { LotId = lotId, Money = amount });
        public BaseMail[] OwnMails() => Mail._allPlayerMails.Values.Where(mail =>
            mail.Header.ReceiverId >= Id + 1 && mail.Header.ReceiverId <= Id + 3).ToArray();
        public void Dispose()
        {
            s_items.SetValue(null, _previousItems);
            s_quests.SetValue(null, _previousQuests);
            s_mail.SetValue(null, _previousMail);
        }
        private static ItemTemplate Template(uint id) => new() { Id = id, MaxCount = 100, BindType = ItemBindType.Normal, FixedGrade = -1, Gradable = true };
        private static TestCharacter Character(uint id) => new()
        {
            Id = id, AccountId = id, Level = 50, Name = $"Auction{id}", Faction = new SystemFaction(), FactionName = "",
            Slots = [], Created = DateTime.UtcNow, Money = 10000, NumInventorySlots = 20, NumBankSlots = 20
        };
        private static void SetField(object owner, string field, object value) => owner.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
        private static T Field<T>(object owner, string field) => (T)owner.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    }

    private sealed class TestCharacter() : Character(new UnitCustomModelParams())
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }
}
