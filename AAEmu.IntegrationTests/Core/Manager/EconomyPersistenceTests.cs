using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Units;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class EconomyPersistenceTests
{
    private static int _nextId = 960000;

    [Fact]
    public void FailedSettlement_RetryCommitsConsumedItemWalletMailAndAuctionTogether()
    {
        var graph = new SaveGraph();
        var character = CreateCharacter(graph.Id);
        var consumed = graph.AddItem(1);
        var auctionItem = graph.AddItem(2);
        var mail = graph.AddMail(3);
        var lot = graph.AddLot(4, auctionItem);
        character.Money = 100;
        Assert.True(graph.Save.TryCommitEconomy([character]));
        var previousUpdated = character.Updated;

        character.Money = 50;
        consumed.Count = 0;
        consumed._holdingContainer = null;
        graph.Container.Items.Remove(consumed);
        mail.Body.CopperCoins = 50;
        lot.BidMoney = 50;
        lot.IsDirty = true;
        var received = graph.AddItem(5);
        Assert.False(graph.Save.TryCommitEconomy([character], _ =>
            throw new InvalidOperationException("Injected failure after every economic writer.")));

        Assert.Equal(100, Read("characters", "money", character.Id));
        Assert.Equal(1, Read("items", "count", consumed.Id));
        Assert.Equal(0, Count("items", received.Id));
        Assert.Equal(0, Read("mails", "money_amount_1", (ulong)mail.Id));
        Assert.Equal(0, Read("auction_house", "bid_money", lot.Id));
        Assert.Equal(previousUpdated, character.Updated);
        Assert.True(consumed.IsDirty);
        Assert.True(received.IsDirty);
        Assert.True(mail.IsDirty);
        Assert.True(lot.IsDirty);

        Assert.True(graph.Save.TryCommitEconomy([character]));
        Assert.Equal(50, Read("characters", "money", character.Id));
        Assert.Equal(0, Count("items", consumed.Id));
        Assert.Equal(1, Count("items", received.Id));
        Assert.Equal(50, Read("mails", "money_amount_1", (ulong)mail.Id));
        Assert.Equal(50, Read("auction_house", "bid_money", lot.Id));
        Assert.False(consumed.IsDirty);
        Assert.False(received.IsDirty);
        Assert.False(mail.IsDirty);
        Assert.False(lot.IsDirty);
        Assert.Same(consumed, graph.Items[consumed.Id]); // ID release belongs to feature completion.
    }

    [Theory]
    [InlineData("item_containers")]
    [InlineData("items")]
    [InlineData("mails")]
    [InlineData("auction_house")]
    public void SqlWriterFailure_AbortsTheBatchAndPreservesDirtyState(string table)
    {
        var graph = new SaveGraph();
        var item = graph.AddItem(1);
        var mail = graph.AddMail(2);
        var lot = graph.AddLot(3, item);
        var trigger = $"economy_fail_{graph.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected economy writer failure'");
        try
        {
            Assert.False(graph.Save.TryCommitEconomy([]));
            Assert.Equal(0, Count("items", item.Id));
            Assert.Equal(0, Count("mails", (ulong)mail.Id));
            Assert.Equal(0, Count("auction_house", lot.Id));
            Assert.True(graph.Container.IsDirty);
            Assert.True(item.IsDirty);
            Assert.True(mail.IsDirty);
            Assert.True(lot.IsDirty);
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }

        Assert.True(graph.Save.TryCommitEconomy([]));
        Assert.False(graph.Container.IsDirty);
        Assert.False(item.IsDirty);
        Assert.False(mail.IsDirty);
        Assert.False(lot.IsDirty);
    }

    [Fact]
    public void LaterItemFailure_RollsBackEarlierItemAndRetriesBoth()
    {
        var graph = new SaveGraph();
        var first = graph.AddItem(1);
        var second = graph.AddItem(2);
        var trigger = $"economy_second_item_{graph.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON items FOR EACH ROW BEGIN IF NEW.id = {second.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected second item failure'; END IF; END");
        try
        {
            Assert.False(graph.Save.TryCommitEconomy([]));
            Assert.Equal(0, Count("items", first.Id));
            Assert.Equal(0, Count("items", second.Id));
            Assert.True(first.IsDirty);
            Assert.True(second.IsDirty);
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }
        Assert.True(graph.Save.TryCommitEconomy([]));
        Assert.Equal(1, Count("items", first.Id));
        Assert.Equal(1, Count("items", second.Id));
    }

    [Fact]
    public void DeletionQueues_SurviveRollbackAndAcknowledgeOnlyWrittenIds()
    {
        var graph = new SaveGraph();
        var item = graph.AddItem(1);
        var mail = graph.AddMail(2);
        var lot = graph.AddLot(3, item);
        Assert.True(graph.Save.TryCommitEconomy([]));
        graph.Items.Remove(item.Id);
        graph.ItemDeletes.Add(item.Id);
        graph.Mail._allPlayerMails.TryRemove(mail.Id, out _);
        graph.MailDeletes.Add(mail.Id);
        graph.Auction.AuctionLots.TryRemove(lot.Id, out _);
        graph.AuctionDeletes.Add((long)lot.Id);

        Assert.False(graph.Save.TryCommitEconomy([], _ => throw new InvalidOperationException("Rollback deletions.")));
        Assert.Equal(1, Count("items", item.Id));
        Assert.Equal(1, Count("mails", (ulong)mail.Id));
        Assert.Equal(1, Count("auction_house", lot.Id));
        Assert.Contains(item.Id, graph.ItemDeletes);
        Assert.Contains(mail.Id, graph.MailDeletes);
        Assert.Contains((long)lot.Id, graph.AuctionDeletes);

        var laterId = graph.Id + 20;
        Assert.True(graph.Save.TryCommitEconomy([], _ =>
        {
            // Work queued after these serializers ran must remain pending.
            graph.ItemDeletes.Add(laterId);
            graph.MailDeletes.Add(laterId);
            graph.AuctionDeletes.Add(laterId);
        }));
        Assert.Equal(0, Count("items", item.Id));
        Assert.Equal(0, Count("mails", (ulong)mail.Id));
        Assert.Equal(0, Count("auction_house", lot.Id));
        Assert.Equal([laterId], graph.ItemDeletes);
        Assert.Equal([(long)laterId], graph.MailDeletes);
        Assert.Equal([(long)laterId], graph.AuctionDeletes.ToArray());
    }

    [Fact]
    public void InvalidCharacter_AbortsEarlierWritesAndAnExplicitOfflineParticipantCanRetry()
    {
        var graph = new SaveGraph();
        var item = graph.AddItem(1);
        var character = CreateCharacter(graph.Id);
        character.ModelParams = null;
        Assert.False(graph.Save.TryCommitEconomy([character]));
        Assert.Equal(0, Count("items", item.Id));
        Assert.True(item.IsDirty);

        character.ModelParams = new UnitCustomModelParams();
        character.Money = 73;
        Assert.True(graph.Save.TryCommitEconomy([character, character]));
        Assert.Equal(73, Read("characters", "money", character.Id));
        Assert.Equal(1, Count("items", item.Id));
    }

    [Fact]
    public void CommitConnectionFailure_PropagatesWithoutAcknowledgingPreparedState()
    {
        var graph = new SaveGraph();
        var item = graph.AddItem(1);
        Assert.ThrowsAny<Exception>(() => graph.Save.TryCommitEconomy([], context =>
        {
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = "SELECT CONNECTION_ID()";
            var connectionId = Convert.ToUInt64(command.ExecuteScalar());
            Execute($"KILL CONNECTION {connectionId}");
        }));
        Assert.True(item.IsDirty);
        Assert.True(graph.Container.IsDirty);
        Assert.Equal(0, Count("items", item.Id));
        Assert.True(graph.Save.TryCommitEconomy([]));
    }

    [Fact]
    public void CommittedContainerDeletion_DeregistersBeforeTheNextCheckpoint()
    {
        var graph = new SaveGraph();
        Assert.True(graph.Save.TryCommitEconomy([]));
        var id = graph.Container.ContainerId;
        lock (SaveManager.PersistenceSyncRoot)
        {
            Assert.True(graph.Save.TryCommitEconomy([], context =>
            {
                using var command = context.Connection.CreateCommand();
                command.Transaction = context.Transaction;
                command.CommandText = "DELETE FROM item_containers WHERE container_id = @id";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }));
            graph.ItemStore.ForgetCommittedItemContainer(graph.Container);
            graph.ItemStore.ForgetCommittedItemContainer(graph.Container);
        }
        Assert.Equal(0UL, graph.Container.ContainerId);
        Assert.Null(graph.ItemStore.GetItemContainerByDbId(id));
        graph.ContainerIds.Verify(manager => manager.ReleaseId((uint)id), Times.Once());
        graph.Container.IsDirty = true;
        Assert.True(graph.Save.TryCommitEconomy([]));
        using var connection = MySQL.CreateConnection();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM item_containers WHERE container_id = @id";
        read.Parameters.AddWithValue("@id", id);
        Assert.Equal(0L, Convert.ToInt64(read.ExecuteScalar()));
    }

    [Theory]
    [InlineData("portal_visited_district", "subzone", "_removedVisitedDistricts")]
    [InlineData("portal_book_coords", "id", "_removedPrivatePortals")]
    [InlineData("friends", "friend_id", "_removedFriends")]
    [InlineData("blocked", "blocked_id", "_removedBlocked")]
    [InlineData("skills", "id", "_removed")]
    [InlineData("quests", "template_id", "_removed")]
    [InlineData("mates", "id", "_removedMates")]
    public void CharacterDeletionQueue_RollbackRetriesAndCommitRetainsLaterIds(string table, string key, string field)
    {
        var graph = new SaveGraph();
        var character = CreateCharacter(graph.Id);
        object child = table switch
        {
            "portal_visited_district" or "portal_book_coords" => character.Portals = new CharacterPortals(character),
            "friends" => character.Friends = new CharacterFriends(character),
            "blocked" => character.Blocked = new CharacterBlocked(character),
            "skills" => character.Skills = new CharacterSkills(character),
            "quests" => character.Quests = new CharacterQuests(character),
            "mates" => character.Mates = new CharacterMates(character),
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var id = graph.Id + 1;
        var seed = table switch
        {
            "portal_visited_district" => $"INSERT INTO {table} (id, subzone, owner) VALUES ({id}, {id}, {character.Id})",
            "portal_book_coords" => $"INSERT INTO {table} (id, name, owner) VALUES ({id}, 'Test', {character.Id})",
            "friends" => $"INSERT INTO {table} (id, friend_id, owner) VALUES ({id}, {id}, {character.Id})",
            "blocked" => $"INSERT INTO {table} (blocked_id, owner) VALUES ({id}, {character.Id})",
            "skills" => $"INSERT INTO {table} (id, level, type, owner) VALUES ({id}, 1, 'Skill', {character.Id})",
            "quests" => $"INSERT INTO {table} (id, template_id, data, status, owner) VALUES ({id}, {id}, X'00', 1, {character.Id})",
            "mates" => $"INSERT INTO {table} (id, item_id, name, xp, level, mileage, hp, mp, owner) VALUES ({id}, {id}, 'Test', 0, 1, 0, 1, 1, {character.Id})",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        Execute(seed);
        var removed = Field<List<uint>>(child, field);
        removed.Add(id);

        Assert.False(graph.Save.TryCommitEconomy([character], _ => throw new InvalidOperationException("Rollback child deletion.")));
        Assert.Equal(1, CountChild());
        Assert.Equal([id], removed);

        var laterId = id + 1;
        Assert.True(graph.Save.TryCommitEconomy([character], _ => removed.Add(laterId)));
        Assert.Equal(0, CountChild());
        Assert.Equal([laterId], removed);

        long CountChild()
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE owner = @owner AND {key} = @id";
            command.Parameters.AddWithValue("@owner", character.Id);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    private static Character CreateCharacter(uint id)
    {
        return new Character(new UnitCustomModelParams())
        {
            Id = id,
            AccountId = id,
            Name = $"Economy{id}",
            Faction = new SystemFaction(),
            FactionName = "",
            Slots = [],
            Created = DateTime.UtcNow
        };
    }

    private static long Count(string table, ulong id)
    {
        return Read(table, "COUNT(*)", id);
    }

    private static long Read(string table, string column, ulong id)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE id = @id";
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

    private static T Field<T>(object owner, string name)
    {
        return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    }

    private sealed class SaveGraph
    {
        public uint Id { get; } = (uint)Interlocked.Add(ref _nextId, 100);
        public Dictionary<ulong, Item> Items { get; } = [];
        public List<ulong> ItemDeletes { get; } = [];
        public ItemManager ItemStore { get; }
        public Mock<IContainerIdManager> ContainerIds { get; } = new();
        public ItemContainer Container { get; }
        public MailManager Mail { get; }
        public AuctionManager Auction { get; }
        public SaveManager Save { get; }
        public List<long> MailDeletes => Field<List<long>>(Mail, "_deletedMailIds");
        public ConcurrentBag<long> AuctionDeletes => (ConcurrentBag<long>)typeof(AuctionManager)
            .GetProperty("DeletedAuctionItemIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Auction)!;

        public SaveGraph()
        {
            var world = new Mock<IWorldManager>();
            world.Setup(manager => manager.GetAllCharacters()).Returns([]);
            world.Setup(manager => manager.GetWorlds()).Returns([]);
            var items = new ItemManager(Mock.Of<ISkillManager>(), Mock.Of<IItemIdManager>(),
                ContainerIds.Object, Mock.Of<ILocalizationManager>(), Mock.Of<ITaskManager>(), world.Object);
            ItemStore = items;
            Container = new ItemContainer(Id, SlotType.Inventory, false, null)
                { ContainerId = Id, ContainerSize = 10 };
            typeof(ItemManager).GetField("_allItems", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(items, Items);
            typeof(ItemManager).GetField("_removedItems", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(items, ItemDeletes);
            typeof(ItemManager).GetField("_allPersistentContainers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(items, new Dictionary<ulong, ItemContainer> { [Container.ContainerId] = Container });
            Mail = new MailManager(Mock.Of<IMailIdManager>(), Mock.Of<INameManager>(), items,
                Mock.Of<ITaskManager>(), world.Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>()),
                Mock.Of<ILocalizationManager>()) { _allPlayerMails = [] };
            Auction = new AuctionManager(items, Mock.Of<INameManager>(), Mock.Of<IAuctionIdManager>(),
                Mock.Of<ILocalizationManager>(), Mock.Of<ITaskManager>());
            Save = new SaveManager(Mock.Of<ITaskManager>(), Mock.Of<IHousingManager>(), Mail, items, Auction,
                Mock.Of<ICrimeManager>(), world.Object, Mock.Of<IZoneManager>());
        }

        public Item AddItem(uint offset)
        {
            var item = new Item
            {
                Id = Id + offset, OwnerId = Id, TemplateId = 1, Count = 1,
                SlotType = SlotType.Inventory, Slot = (int)offset, CreateTime = DateTime.UtcNow,
                _holdingContainer = Container
            };
            Items.Add(item.Id, item);
            Container.Items.Add(item);
            return item;
        }

        public BaseMail AddMail(uint offset)
        {
            var mail = new BaseMail
            {
                Id = Id + offset, Title = "Economy persistence test", ReceiverName = "Receiver",
                OpenDate = DateTime.UtcNow
            };
            mail.Header.SenderName = "Sender";
            mail.Header.ReceiverId = Id;
            mail.Body.Text = "Test";
            mail.Body.SendDate = DateTime.UtcNow;
            mail.Body.RecvDate = DateTime.UtcNow;
            Mail._allPlayerMails.TryAdd(mail.Id, mail);
            return mail;
        }

        public AuctionLot AddLot(uint offset, Item item)
        {
            var lot = new AuctionLot
            {
                Id = Id + offset, Item = item, ClientId = Id, ClientName = "Seller", BidderName = "",
                PostDate = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(6), IsDirty = true
            };
            Auction.AuctionLots.TryAdd(lot.Id, lot);
            return lot;
        }
    }
}
