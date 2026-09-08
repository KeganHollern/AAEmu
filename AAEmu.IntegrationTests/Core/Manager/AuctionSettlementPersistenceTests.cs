using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Auction;
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
public sealed class AuctionSettlementPersistenceTests
{
    private static int _nextId = 980000;

    [Fact]
    public void PostedBid_ReloadsAndFailedBuyoutRetriesWithoutDuplicatingEscrowOrProceeds()
    {
        using var posted = new AuctionGraph();
        var item = posted.AddItem();
        posted.Post(item);
        var lotId = posted.Auction.AuctionLots.Keys.Single();
        posted.Bid(posted.Other, lotId, 500);
        Assert.Equal(9980, Read("characters", "money", posted.Seller.Id));
        Assert.Equal(9500, Read("characters", "money", posted.Other.Id));

        using var restored = new AuctionGraph(posted.Id);
        var lot = restored.Auction.AuctionLots[lotId];
        var restoredItem = restored.Items.GetItemByItemId(item.Id);
        Assert.Same(restoredItem, lot.Item);
        Assert.Equal(500, lot.BidMoney);
        Assert.Equal(restored.Other.Id, lot.BidderId);
        Assert.Same(restored.Seller.Inventory.AuctionAttachments, restoredItem._holdingContainer);
        var trigger = $"auction_mail_failure_{posted.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON mails FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected auction mail failure'");
        try
        {
            restored.Bid(restored.Buyer, lotId, 2000);
            Assert.Same(lot, restored.Auction.AuctionLots[lotId]);
            Assert.Same(restored.Seller.Inventory.AuctionAttachments, restoredItem._holdingContainer);
            Assert.Equal(10000, restored.Buyer.Money);
            Assert.Equal(500, Read("auction_house", "bid_money", lotId));
            Assert.Equal((long)restored.Seller.Id, Read("items", "owner", item.Id));
            Assert.Empty(restored.OwnMails());
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }

        restored.Bid(restored.Buyer, lotId, int.MaxValue);
        using var settled = new AuctionGraph(posted.Id);
        Assert.DoesNotContain(lotId, settled.Auction.AuctionLots.Keys);
        Assert.Equal(8000, settled.Buyer.Money);
        Assert.Equal(9500, settled.Other.Money);
        Assert.Equal(9980, settled.Seller.Money);
        var deliveredItem = settled.Items.GetItemByItemId(item.Id);
        var mails = settled.OwnMails();
        Assert.Equal(3, mails.Length);
        Assert.Equal(500, mails.Single(mail => mail.MailType == MailType.AucBidFail).Body.CopperCoins);
        Assert.Equal(1800, mails.Single(mail => mail.MailType == MailType.AucOffSuccess).Body.CopperCoins);
        Assert.Same(deliveredItem, mails.Single(mail => mail.MailType == MailType.AucBidWin).Body.Attachments.Single());
        Assert.Same(settled.Buyer.Inventory.MailAttachments, deliveredItem._holdingContainer);
        Assert.Equal(item.Id, deliveredItem.Id);
        Assert.Equal(item.Count, deliveredItem.Count);
        Assert.Equal(item.Grade, deliveredItem.Grade);
        Assert.Equal(item.UccId, deliveredItem.UccId);
        Assert.Equal(item.ExpirationTime, deliveredItem.ExpirationTime);
        Assert.Equal(item.ExpirationOnlineMinutesLeft, deliveredItem.ExpirationOnlineMinutesLeft);
        settled.Bid(settled.Other, lotId, 2000);
        Assert.Equal(3, settled.OwnMails().Length);
        Assert.Equal(9500, settled.Other.Money);
    }

    [Fact]
    public void FailedListingWrite_RestoresBagAndFeeThenPublishesOnceOnRetry()
    {
        using var graph = new AuctionGraph();
        var item = graph.AddItem();
        Assert.True(graph.Save.TryCommitEconomy([graph.Seller]));
        var trigger = $"auction_post_failure_{graph.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON auction_house FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected auction row failure'");
        try
        {
            graph.Post(item);
            Assert.Empty(graph.Auction.AuctionLots.Where(entry => entry.Value.ClientId == graph.Seller.Id));
            Assert.Same(graph.Seller.Inventory.Bag, item._holdingContainer);
            Assert.Equal(10000, graph.Seller.Money);
            Assert.Equal((long)SlotType.Inventory, Read("items", "slot_type", item.Id));
            Assert.Equal(10000, Read("characters", "money", graph.Seller.Id));
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }
        graph.Post(item);
        using var restored = new AuctionGraph(graph.Id);
        var lot = restored.Auction.AuctionLots.Values.Single(value => value.ClientId == restored.Seller.Id);
        Assert.Equal(item.Id, lot.Item.Id);
        Assert.Equal(9980, restored.Seller.Money);
        Assert.Same(restored.Seller.Inventory.AuctionAttachments, lot.Item._holdingContainer);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("unbid-expiry")]
    [InlineData("bid-expiry")]
    public void ReturnOrExpiry_ReloadsExactlyOneTerminalSettlement(string action)
    {
        using var graph = new AuctionGraph();
        var item = graph.AddItem();
        graph.Post(item);
        var lot = graph.Auction.AuctionLots.Values.Single();
        if (action == "bid-expiry") graph.Bid(graph.Buyer, lot.Id, 500);
        if (action == "cancel") graph.Auction.CancelAuctionLot(graph.Seller, lot.Id);
        else
        {
            lot.EndTime = DateTime.UtcNow.AddSeconds(-1);
            graph.Auction.UpdateAuctionHouse();
        }
        using var restored = new AuctionGraph(graph.Id);
        Assert.DoesNotContain(lot.Id, restored.Auction.AuctionLots.Keys);
        var mailedItem = restored.Items.GetItemByItemId(item.Id);
        var mails = restored.OwnMails();
        Assert.Equal(action == "bid-expiry" ? 2 : 1, mails.Length);
        Assert.Same(mailedItem, mails.Single(mail => mail.Body.Attachments.Count != 0).Body.Attachments.Single());
        Assert.Equal((ulong)(action == "bid-expiry" ? restored.Buyer.Id : restored.Seller.Id), mailedItem.OwnerId);
        if (action == "bid-expiry")
            Assert.Equal(450, mails.Single(mail => mail.MailType == MailType.AucOffSuccess).Body.CopperCoins);
        restored.Auction.UpdateAuctionHouse();
        restored.Auction.CancelAuctionLot(restored.Seller, lot.Id);
        Assert.Equal(mails.Length, restored.OwnMails().Length);
    }

    private static long Read(string table, string field, ulong id)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {field} FROM {table} WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class AuctionGraph : IDisposable
    {
        private static readonly FieldInfo s_items = typeof(Singleton<ItemManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo s_quests = typeof(Singleton<QuestManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previousItems = s_items.GetValue(null);
        private readonly object _previousQuests = s_quests.GetValue(null);
        public uint Id { get; }
        public ItemManager Items { get; }
        public MailManager Mail { get; }
        public AuctionManager Auction { get; }
        public SaveManager Save { get; }
        public TestCharacter Seller { get; }
        public TestCharacter Buyer { get; }
        public TestCharacter Other { get; }

        public AuctionGraph(uint? restoreId = null)
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
            Items = new ItemManager(Mock.Of<ISkillManager>(), Mock.Of<IItemIdManager>(),
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
        public void Dispose() { s_items.SetValue(null, _previousItems); s_quests.SetValue(null, _previousQuests); }
        private static ItemTemplate Template(uint id) => new() { Id = id, MaxCount = 100, BindType = ItemBindType.Normal, FixedGrade = -1, Gradable = true };
        private static TestCharacter Character(uint id) => new()
        {
            Id = id, AccountId = id, Name = $"Auction{id}", Faction = new SystemFaction(), FactionName = "",
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
