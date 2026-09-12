using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.Dominions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class EconomyPersistenceTests
{
    private static int _nextId = 960000;

    [Fact]
    public void PriestPurchase_BuffInsertFailureRestoresWallet_AndRetryPersistsBoth()
    {
        var skillField = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldSkills = skillField.GetValue(null);
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(skills, new Dictionary<uint, SkillTemplate>());
        skillField.SetValue(null, skills);
        try
        {
            var graph = new SaveGraph();
            var character = CreateCharacter(graph.Id);
            character.Money = 10000;
            Assert.True(graph.Save.TryCommitEconomy([character]));
            var effects = Field<List<Buff>>(character.Buffs, "_effects");
            var buff = new Buff(character, character, new SkillCasterUnit(character.ObjId),
                new BuffTemplate { Id = 239, SaveRuleId = BuffSaveRuleType.Normal }, null, DateTime.UtcNow)
                { State = EffectState.Acting, Duration = 1800000 };
            var trigger = $"reject_priest_{graph.Id}";
            Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON character_active_buffs FOR EACH ROW " +
                $"BEGIN IF NEW.character_id={character.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected priest buff failure'; END IF; END");
            try
            {
                Assert.False(Purchase());
                Assert.Equal(10000, character.Money);
                Assert.Equal(10000, Read("characters", "money", character.Id));
                Assert.Empty(effects);
                Assert.Equal(0, PersistedBuffs());
            }
            finally
            {
                Execute($"DROP TRIGGER {trigger}");
            }
            Assert.True(Purchase());
            Assert.Equal(5000, character.Money);
            Assert.Equal(5000, Read("characters", "money", character.Id));
            Assert.Same(buff, Assert.Single(effects));
            Assert.Equal(1, PersistedBuffs());

            bool Purchase() => character.CompletePriestPurchase(5000,
                () => effects.Add(buff), () => effects.Remove(buff),
                () => graph.Save.TryCommitEconomy([character]));
            long PersistedBuffs()
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={character.Id} AND buff_id=239";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
        finally { skillField.SetValue(null, oldSkills); }
    }

    [Theory]
    [InlineData("specialty_demand")]
    [InlineData("house_tax_receipts")]
    [InlineData("dominion_states")]
    public void WorldEconomyMigration_CanRepeatWithoutChangingRows(string suffix)
    {
        var update = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SQL", "updates",
            $"2026-09-12_aaemu_game_{suffix}.sql"));
        Execute(update);
        Execute(update);
    }

    [Fact]
    public void DominionBalance_ReloadsCommittedStateAndIgnoresRolledBackUpdate()
    {
        var state = DominionState.Unclaimed(33, 1);
        using var connection = MySQL.CreateConnection();
        using (var transaction = connection.BeginTransaction())
        {
            DominionStateStore.Save(connection, transaction, state);
            transaction.Commit();
        }
        using (var transaction = connection.BeginTransaction())
        {
            DominionStateStore.Save(connection, transaction, state with { HouseTaxBalance = 500 });
            transaction.Rollback();
        }
        Assert.Equal(state, DominionStateStore.Load(connection)[33]);
        using (var transaction = connection.BeginTransaction())
        {
            DominionStateStore.Save(connection, transaction, state with { HouseTaxBalance = 250 });
            transaction.Commit();
        }
        using var reload = MySQL.CreateConnection();
        Assert.Equal(state with { HouseTaxBalance = 250 }, DominionStateStore.Load(reload)[33]);
    }

    [Fact]
    public void PackSettlement_LaborDemandAndMailRollbackTogether_AndReloadDemand()
    {
        var graph = new SaveGraph();
        var character = CreateCharacter(graph.Id);
        character.InitializeLaborCache(100, DateTime.UtcNow);
        Execute($"INSERT INTO accounts (account_id, labor) VALUES ({character.AccountId}, 100)");
        var pack = graph.AddItem(1);
        Assert.True(graph.Save.TryCommitEconomy([character]));
        var accountField = typeof(Singleton<AccountManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = accountField.GetValue(null);
        var accounts = new AccountManager(Mock.Of<ITickManager>(), Mock.Of<ITimedRewardsManager>(), TimeProvider.System);
        accountField.SetValue(null, accounts);
        var formulaField = typeof(Singleton<FormulaManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousFormulas = formulaField.GetValue(null);
        var formulas = new FormulaManager();
        typeof(FormulaManager).GetField("_formulas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(formulas, new Dictionary<uint, Formula>());
        formulaField.SetValue(null, formulas);
        try
        {
            lock (SaveManager.PersistenceSyncRoot)
            lock (accounts.GetAccountSyncRoot(character.AccountId))
            {
                var config = new SpecialtyConfig();
                var now = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
                var demand = SpecialtyDemand.Create(graph.Id, 2, now, config).RecordSale(now, config);
                pack.Count = 0;
                pack._holdingContainer = null;
                graph.Container.Items.Remove(pack);
                var mail = graph.AddMail(2);
                mail.Body.CopperCoins = 1234;
                using (var labor = new CharacterLaborMutation(character))
                {
                    Assert.True(labor.TryConsume(60, 0));
                    Assert.False(graph.Save.TryCommitEconomy([character], context =>
                    {
                        labor.Save(context);
                        SpecialtyDemandStore.Save(context.Connection, context.Transaction, demand);
                        throw new InvalidOperationException("Fail after labor and demand writes.");
                    }));
                }
                Assert.Equal(100, character.LaborPower);
                Assert.Equal(1, Count("items", pack.Id));
                Assert.Equal(0, Count("mails", (ulong)mail.Id));
                using (var connection = MySQL.CreateConnection())
                    Assert.DoesNotContain(SpecialtyDemandStore.Load(connection), value => value.ItemId == graph.Id);

                using (var labor = new CharacterLaborMutation(character))
                {
                    Assert.True(labor.TryConsume(60, 0));
                    Assert.True(graph.Save.TryCommitEconomy([character], context =>
                    {
                        labor.Save(context);
                        SpecialtyDemandStore.Save(context.Connection, context.Transaction, demand);
                    }));
                    labor.PreservePreparedState();
                }
                Assert.Equal(40, character.LaborPower);
                Assert.Equal(0, Count("items", pack.Id));
                Assert.Equal(1234, Read("mails", "money_amount_1", (ulong)mail.Id));
                using var restoredConnection = MySQL.CreateConnection();
                Assert.Equal(demand, Assert.Single(SpecialtyDemandStore.Load(restoredConnection), value => value.ItemId == graph.Id));
                using var readLabor = restoredConnection.CreateCommand();
                readLabor.CommandText = $"SELECT labor FROM accounts WHERE account_id={character.AccountId}";
                Assert.Equal(40, Convert.ToInt32(readLabor.ExecuteScalar()));
            }
        }
        finally
        {
            accountField.SetValue(null, previous);
            formulaField.SetValue(null, previousFormulas);
        }
    }

    [Fact]
    public void HouseTaxReceipt_IsAtomicWithHouseAndWallet_AndRejectsReplay()
    {
        var graph = new SaveGraph();
        var character = CreateCharacter(graph.Id);
        character.Money = 1000;
        var before = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var house = new House
        {
            Id = graph.Id, AccountId = graph.Id, OwnerId = graph.Id, TemplateId = 1,
            Name = "Tax receipt test", Template = new HousingTemplate { HousingBindingDoodad = [] },
            PlaceDate = before.AddDays(-14), ProtectionEndDate = before,
            Faction = new SystemFaction(), IsDirty = true
        };
        Assert.True(graph.Save.TryCommitEconomy([character], context => house.Save(context)));
        character.Money -= 110;
        house.ProtectionEndDate = before.AddDays(7);
        void WritePayment(PersistenceSaveContext context)
        {
            house.Save(context);
            HouseTaxReceipt.Save(context, graph.Id, house, character.Id, 110, false, before, before.AddDays(-1), 10);
        }
        Assert.False(graph.Save.TryCommitEconomy([character], context =>
        {
            WritePayment(context);
            throw new InvalidOperationException("Fail after receipt write.");
        }));
        Assert.Equal(1000, Read("characters", "money", character.Id));
        Assert.True(graph.Save.TryCommitEconomy([character], WritePayment));
        Assert.Equal(890, Read("characters", "money", character.Id));
        character.Money -= 100;
        Assert.False(graph.Save.TryCommitEconomy([character], WritePayment));
        Assert.Equal(890, Read("characters", "money", character.Id));
    }

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
        var stopped = false;
        graph.Save.StopForConsistencyFailure = (_, _) => stopped = true;
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
        Assert.True(stopped);
        Assert.Throws<InvalidOperationException>(() => graph.Save.TryCommitEconomy([]));
        Assert.False(graph.Save.DoSave());
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
        string Seed(uint childId) => table switch
        {
            "portal_visited_district" => $"INSERT INTO {table} (id, subzone, owner) VALUES ({childId}, {childId}, {character.Id})",
            "portal_book_coords" => $"INSERT INTO {table} (id, name, owner) VALUES ({childId}, 'Test', {character.Id})",
            "friends" => $"INSERT INTO {table} (id, friend_id, owner) VALUES ({childId}, {childId}, {character.Id})",
            "blocked" => $"INSERT INTO {table} (blocked_id, owner) VALUES ({childId}, {character.Id})",
            "skills" => $"INSERT INTO {table} (id, level, type, owner) VALUES ({childId}, 1, 'Skill', {character.Id})",
            "quests" => $"INSERT INTO {table} (id, template_id, data, status, owner) VALUES ({childId}, {childId}, X'00', 1, {character.Id})",
            "mates" => $"INSERT INTO {table} (id, item_id, name, xp, level, mileage, hp, mp, owner) VALUES ({childId}, {childId}, 'Test', 0, 1, 0, 1, 1, {character.Id})",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        Execute(Seed(id));
        var removedList = table == "blocked" ? null : Field<List<uint>>(child, field);
        var removedVersions = table == "blocked" ? Field<Dictionary<uint, long>>(child, field) : null;
        long sequence = 0;
        void QueueRemoval(uint value)
        {
            if (removedVersions != null) removedVersions[value] = ++sequence;
            else removedList!.Add(value);
        }
        uint[] RemovedIds() => removedVersions?.Keys.ToArray() ?? removedList!.ToArray();
        QueueRemoval(id);

        Assert.False(graph.Save.TryCommitEconomy([character], _ => throw new InvalidOperationException("Rollback child deletion.")));
        Assert.Equal(1, CountChild(id));
        Assert.Equal([id], RemovedIds());

        var laterId = id + 1;
        Execute(Seed(laterId));
        Assert.True(graph.Save.TryCommitEconomy([character], _ =>
        {
            Assert.Equal([id], RemovedIds());
            QueueRemoval(laterId);
        }));
        Assert.Equal(0, CountChild(id));
        Assert.Equal(1, CountChild(laterId));
        Assert.Equal([laterId], RemovedIds());

        Assert.True(graph.Save.TryCommitEconomy([character]));
        Assert.Equal(0, CountChild(laterId));
        Assert.Empty(RemovedIds());

        long CountChild(uint childId)
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE owner = @owner AND {key} = @id";
            command.Parameters.AddWithValue("@owner", character.Id);
            command.Parameters.AddWithValue("@id", childId);
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
                Mock.Of<ILocalizationManager>(), Mock.Of<ITaskManager>(), Mail,
                new Lazy<ISaveManager>(() => Save));
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
