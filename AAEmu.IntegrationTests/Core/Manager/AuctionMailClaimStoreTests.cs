using System.Reflection;
using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class AuctionMailClaimStoreTests : IAsyncLifetime
{
    private static readonly DateTime ClaimTime = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsync("""
            DROP TRIGGER IF EXISTS `fail_auction_achievement_save`;
            TRUNCATE TABLE `auction_mail_claims`;
            TRUNCATE TABLE `mails`;
            TRUNCATE TABLE `items`;
            TRUNCATE TABLE `characters`;
            TRUNCATE TABLE `accounts`;
            TRUNCATE TABLE `abilities`;
            TRUNCATE TABLE `actabilities`;
            TRUNCATE TABLE `character_achievement_records`;
            TRUNCATE TABLE `character_achievements`;
            INSERT INTO `accounts` (`account_id`,`labor`) VALUES (3,10);
            INSERT INTO `characters`
                (`id`,`account_id`,`name`,`race`,`gender`,`unit_model_params`,`level`,`experience`,
                 `recoverable_exp`,`hp`,`mp`,`consumed_lp`,`ability1`,`ability2`,`ability3`,
                 `world_id`,`zone_id`,`x`,`y`,`z`,`faction_id`,`faction_name`,`expedition_id`,
                 `family`,`dead_count`,`rez_wait_duration`,`rez_penalty_duration`,`money`,
                 `auto_use_aapoint`,`prev_point`,`point`,`gift`,`expanded_expert`,`slots`)
            VALUES (7,3,'auction-tester',1,1,X'',1,0,0,100,100,0,1,2,3,
                    1,1,0,0,0,1,'',0,0,0,0,0,1000,0,0,0,0,0,X'');
            INSERT INTO `character_achievement_records` (`character_id`,`record_id`,`amount`)
            VALUES (7,100,1);
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync("DROP TRIGGER IF EXISTS `fail_auction_achievement_save`;");
    }

    [Fact]
    public async Task Sale_CommitsMailMoneyLaborProgressAndReceipt_ThenReplaysAfterRestart()
    {
        var plan = CreateSalePlan();
        await SeedMailAsync(plan.Mail);
        var achievements = LoadAchievements(plan.Character);
        await ResetDurableAchievementAsync();
        var store = new MySqlAuctionMailClaimStore();

        Assert.Equal(AuctionMailClaimPersistenceResult.Created, store.Persist(plan, achievements));
        Assert.Equal(1250, await ScalarAsync("SELECT `money` FROM `characters` WHERE `id`=7"));
        Assert.Equal(9, await ScalarAsync("SELECT `labor` FROM `accounts` WHERE `account_id`=3"));
        Assert.Equal(1, await ScalarAsync("SELECT `consumed_lp` FROM `characters` WHERE `id`=7"));
        Assert.Equal(15, await ScalarAsync("SELECT `experience` FROM `characters` WHERE `id`=7"));
        Assert.Equal(15, await ScalarAsync("SELECT `exp` FROM `abilities` WHERE `owner`=7 AND `id`=1"));
        Assert.Equal(1, await ScalarAsync("SELECT `point` FROM `actabilities` WHERE `owner`=7 AND `id`=1"));
        Assert.Equal(0, await ScalarAsync("SELECT `money_amount_1` FROM `mails` WHERE `id`=20001"));
        Assert.Equal(0, await ScalarAsync("SELECT `attachment_count` FROM `mails` WHERE `id`=20001"));
        Assert.Equal(1, await ScalarAsync("SELECT `amount` FROM `character_achievement_records` WHERE `character_id`=7 AND `record_id`=100"));

        var restartedStore = new MySqlAuctionMailClaimStore();
        Assert.Equal(plan.Receipt, restartedStore.FindReceipt(plan.Mail.Id, 7));
        Assert.Null(restartedStore.FindReceipt(plan.Mail.Id, 8));
        // A replay must not restore an old snapshot over changes made after the first claim.
        await ExecuteAsync("UPDATE `characters` SET `money`=1400 WHERE `id`=7;");
        Assert.Equal(AuctionMailClaimPersistenceResult.Replay, restartedStore.Persist(plan, achievements));
        Assert.Equal(1400, await ScalarAsync("SELECT `money` FROM `characters` WHERE `id`=7"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM `auction_mail_claims`"));
    }

    [Fact]
    public async Task Sale_AchievementWriteFailure_RollsBackEveryClaimWrite_AndAllowsRetry()
    {
        var plan = CreateSalePlan();
        await SeedMailAsync(plan.Mail);
        var achievements = LoadAchievements(plan.Character);
        await ResetDurableAchievementAsync();
        await ExecuteAsync("""
            CREATE TRIGGER `fail_auction_achievement_save`
            BEFORE INSERT ON `character_achievement_records`
            FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='simulated achievement failure';
            """);
        var store = new MySqlAuctionMailClaimStore();

        Assert.Throws<MySqlException>(() => store.Persist(plan, achievements));
        Assert.Equal(1000, await ScalarAsync("SELECT `money` FROM `characters` WHERE `id`=7"));
        Assert.Equal(10, await ScalarAsync("SELECT `labor` FROM `accounts` WHERE `account_id`=3"));
        Assert.Equal(0, await ScalarAsync("SELECT `consumed_lp` FROM `characters` WHERE `id`=7"));
        Assert.Equal(0, await ScalarAsync("SELECT `experience` FROM `characters` WHERE `id`=7"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM `abilities`"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM `actabilities`"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM `mails`"));
        Assert.Equal(250, await ScalarAsync("SELECT `money_amount_1` FROM `mails` WHERE `id`=20001"));
        Assert.Equal(1, await ScalarAsync("SELECT `attachment_count` FROM `mails` WHERE `id`=20001"));
        Assert.Equal((int)MailStatus.Unread, await ScalarAsync("SELECT `status` FROM `mails` WHERE `id`=20001"));
        Assert.Equal(0, await ScalarAsync("SELECT `amount` FROM `character_achievement_records` WHERE `character_id`=7 AND `record_id`=100"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM `auction_mail_claims`"));

        await ExecuteAsync("DROP TRIGGER `fail_auction_achievement_save`;");
        Assert.Equal(AuctionMailClaimPersistenceResult.Created, store.Persist(plan, achievements));
        Assert.Equal(1250, await ScalarAsync("SELECT `money` FROM `characters` WHERE `id`=7"));
        Assert.Equal(1, await ScalarAsync("SELECT `amount` FROM `character_achievement_records` WHERE `character_id`=7 AND `record_id`=100"));
        Assert.Equal(0, await ScalarAsync("SELECT `money_amount_1` FROM `mails` WHERE `id`=20001"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buy_PersistsDestinationAndClearsAttachment_WithDurableReplay(bool mergeStack)
    {
        var plan = CreateBuyPlan(mergeStack);
        await SeedMailAsync(plan.Mail);
        var source = plan.SourceItem;
        await ExecuteAsync($"""
            INSERT INTO `items` (`id`,`type`,`template_id`,`slot_type`,`slot`,`count`,`lifespan_mins`,`owner`,`flags`,`grade`)
            VALUES ({source.Id},'AAEmu.Game.Models.Game.Items.Item',20,{(int)SlotType.Mail},0,5,0,7,0,2);
            """);
        if (mergeStack)
        {
            await ExecuteAsync($"""
                INSERT INTO `items` (`id`,`type`,`template_id`,`container_id`,`slot_type`,`slot`,`count`,`lifespan_mins`,`owner`,`flags`,`grade`)
                VALUES (30002,'AAEmu.Game.Models.Game.Items.Item',20,700,{(int)SlotType.Inventory},2,10,0,7,0,2);
                """);
        }
        Assert.Equal(mergeStack ? 2 : 1, await ScalarAsync("SELECT COUNT(*) FROM `items`"));
        Assert.Equal(source.Id, (ulong)await ScalarAsync("SELECT `attachment0` FROM `mails` WHERE `id`=20002"));
        var store = new MySqlAuctionMailClaimStore();
        var achievements = LoadAchievements(plan.Character);
        await ResetDurableAchievementAsync();

        Assert.Equal(AuctionMailClaimPersistenceResult.Created, store.Persist(plan, achievements));
        var destinationId = plan.DestinationStack?.Id ?? source.Id;
        Assert.Equal(plan.DestinationCountAfter, await ScalarAsync($"SELECT `count` FROM `items` WHERE `id`={destinationId}"));
        Assert.Equal((int)SlotType.Inventory, await ScalarAsync($"SELECT `slot_type` FROM `items` WHERE `id`={destinationId}"));
        Assert.Equal(700, await ScalarAsync($"SELECT `container_id` FROM `items` WHERE `id`={destinationId}"));
        Assert.Equal(7, await ScalarAsync($"SELECT `owner` FROM `items` WHERE `id`={destinationId}"));
        Assert.Equal(2, await ScalarAsync($"SELECT `slot` FROM `items` WHERE `id`={destinationId}"));
        // The schema uses TINYINT(1), which ExecuteScalar otherwise exposes as a Boolean.
        Assert.Equal(2, await ScalarAsync($"SELECT CAST(`grade` AS UNSIGNED) FROM `items` WHERE `id`={destinationId}"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM `items`"));
        Assert.Equal(0, await ScalarAsync("SELECT `attachment0` FROM `mails` WHERE `id`=20002"));
        Assert.Equal(0, await ScalarAsync("SELECT `attachment_count` FROM `mails` WHERE `id`=20002"));
        Assert.Equal(1, await ScalarAsync("SELECT `amount` FROM `character_achievement_records` WHERE `character_id`=7 AND `record_id`=100"));
        Assert.Equal(plan.Receipt, new MySqlAuctionMailClaimStore().FindReceipt(plan.Mail.Id, 7));
        Assert.Equal(AuctionMailClaimPersistenceResult.Replay, store.Persist(plan, achievements));
        Assert.Equal(plan.DestinationCountAfter, await ScalarAsync($"SELECT `count` FROM `items` WHERE `id`={destinationId}"));
    }

    [Fact]
    public async Task ConcurrentClaims_CommitOnceAndReplayTheOtherTransaction()
    {
        var first = CreateSalePlan();
        var second = CreateSalePlan();
        await SeedMailAsync(first.Mail);
        var firstAchievements = LoadAchievements(first.Character);
        var secondAchievements = LoadAchievements(second.Character);
        await ResetDurableAchievementAsync();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var firstTask = Task.Run(() => Claim(first, firstAchievements));
        var secondTask = Task.Run(() => Claim(second, secondAchievements));
        var bothReady = ready.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();
        Assert.True(bothReady);

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.Single(results, result => result == AuctionMailClaimPersistenceResult.Created);
        Assert.Single(results, result => result == AuctionMailClaimPersistenceResult.Replay);
        Assert.Equal(1250, await ScalarAsync("SELECT `money` FROM `characters` WHERE `id`=7"));
        Assert.Equal(9, await ScalarAsync("SELECT `labor` FROM `accounts` WHERE `account_id`=3"));
        Assert.Equal(1, await ScalarAsync("SELECT `amount` FROM `character_achievement_records` WHERE `character_id`=7 AND `record_id`=100"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM `auction_mail_claims`"));

        AuctionMailClaimPersistenceResult Claim(AuctionSaleClaimPlan plan, CharacterAchievements achievements)
        {
            ready.Signal();
            release.Wait(TestContext.Current.CancellationToken);
            return new MySqlAuctionMailClaimStore().Persist(plan, achievements);
        }
    }

    [Fact]
    public async Task ReceiptCollision_WithDifferentAmount_IsRejected()
    {
        var plan = CreateSalePlan();
        await SeedMailAsync(plan.Mail);
        var store = new MySqlAuctionMailClaimStore();
        var achievements = LoadAchievements(plan.Character);
        await ResetDurableAchievementAsync();
        Assert.Equal(AuctionMailClaimPersistenceResult.Created, store.Persist(plan, achievements));
        var collision = plan with { Receipt = plan.Receipt with { MoneyAmount = 251 } };

        Assert.Throws<InvalidOperationException>(() => store.Persist(collision, achievements));
        Assert.Equal(plan.Receipt, store.FindReceipt(plan.Mail.Id, 7));
    }

    private static Character CreateCharacter() => new(null) { Id = 7, AccountId = 3, Name = "auction-tester" };

    private static BaseMail CreateMail(long id, MailType type) => new()
    {
        Id = id,
        MailType = type,
        Title = "Auction result",
        ReceiverName = "auction-tester",
        OpenDate = DateTime.UnixEpoch,
        Header = { Status = MailStatus.Unread, Attachments = 1, ReceiverId = 7, SenderName = "Auction" },
        Body = { Text = "Auction result", SendDate = ClaimTime, RecvDate = ClaimTime }
    };

    private static AuctionSaleClaimPlan CreateSalePlan()
    {
        var character = CreateCharacter();
        var mail = CreateMail(20001, MailType.AucOffSuccess);
        mail.Body.CopperCoins = 250;
        var receipt = new AuctionMailClaimReceipt(mail.Id, AuctionMailClaimType.SaleMoney, 7, null, null, null, null, 250);
        return new AuctionSaleClaimPlan(character, mail, receipt, MailStatus.Read, ClaimTime, 0, [], ClaimTime,
            1000, 1250, 10, 9, 0, 15, 15, 1, 1, 1, 1, 0, 1, 1, 0,
            new Dictionary<AbilityType, int> { [(AbilityType)1] = 15 },
            new Dictionary<AbilityType, byte> { [(AbilityType)1] = 1 });
    }

    private static AuctionBuyClaimPlan CreateBuyPlan(bool mergeStack)
    {
        var character = CreateCharacter();
        var bag = new ItemContainer(7, SlotType.Inventory, false, character) { ContainerId = 700, ContainerSize = 10 };
        var inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        typeof(Inventory).GetField("<Bag>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inventory, bag);
        character.Inventory = inventory;
        var template = new ItemTemplate { Id = 20, Name = "Auction item", MaxCount = 100 };
        var source = new Item(30001, template, 5) { OwnerId = 7, SlotType = SlotType.Mail, Slot = 0, Grade = 2 };
        var destination = mergeStack
            ? new Item(30002, template, 10) { OwnerId = 7, SlotType = SlotType.Inventory, Slot = 2, Grade = 2, _holdingContainer = bag }
            : null;
        if (destination != null)
            bag.Items.Add(destination);
        var mail = CreateMail(20002, MailType.AucBidWin);
        mail.Body.Attachments.Add(source);
        var receipt = new AuctionMailClaimReceipt(mail.Id, AuctionMailClaimType.BuyItem, 7, source.Id, 5, SlotType.Mail, 0, null);
        return new AuctionBuyClaimPlan(character, mail, receipt, MailStatus.Read, ClaimTime, 0, [], ClaimTime,
            source, destination, 2, mergeStack ? 10 : 0, mergeStack ? 15 : 5, source.ItemFlags, source.ItemFlags);
    }

    private static CharacterAchievements LoadAchievements(Character character)
    {
        using var connection = MySQL.CreateConnection();
        character.Achievements.Load(connection);
        return character.Achievements;
    }

    private static Task ResetDurableAchievementAsync()
    {
        // Loaded characters retain amount 1. Restore the durable baseline to 0 so the store
        // must persist the pending amount, including when two independently loaded plans race.
        return ExecuteAsync("UPDATE `character_achievement_records` SET `amount`=0 WHERE `character_id`=7 AND `record_id`=100;");
    }

    private static async Task SeedMailAsync(BaseMail mail)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO `mails`
                (`id`,`type`,`status`,`title`,`text`,`sender_name`,`receiver_id`,`receiver_name`,
                 `open_date`,`send_date`,`received_date`,`returned`,`extra`,
                 `money_amount_1`,`money_amount_2`,`money_amount_3`,`attachment_count`,`attachment0`)
            VALUES (@id,@type,@status,'Auction result','Auction result','Auction',7,'auction-tester',
                    @open_date,@send_date,@received_date,0,0,@money,0,0,1,@attachment);
            """;
        command.Parameters.AddWithValue("@id", mail.Id);
        command.Parameters.AddWithValue("@type", (int)mail.MailType);
        command.Parameters.AddWithValue("@status", (int)MailStatus.Unread);
        command.Parameters.AddWithValue("@open_date", DateTime.UnixEpoch);
        command.Parameters.AddWithValue("@send_date", ClaimTime);
        command.Parameters.AddWithValue("@received_date", ClaimTime);
        command.Parameters.AddWithValue("@money", mail.Body.CopperCoins);
        command.Parameters.AddWithValue("@attachment", mail.Body.Attachments.FirstOrDefault()?.Id ?? 0UL);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
