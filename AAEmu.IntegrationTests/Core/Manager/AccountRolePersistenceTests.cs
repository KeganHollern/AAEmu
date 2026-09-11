using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;

using Moq;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class AccountRolePersistenceTests : IAsyncLifetime
{
    private const uint AdminId = 4_100_440;
    private const uint ModeratorId = 4_100_441;
    private const uint PlayerId = 4_100_442;
    private const uint MissingId = 4_100_443;

    public ValueTask InitializeAsync()
    {
        Cleanup();
        Execute($"""
            INSERT INTO `accounts` (`account_id`, `role`, `labor`, `credits`, `loyalty`) VALUES
                ({AdminId}, 2, 17, 18, 19),
                ({ModeratorId}, 1, 27, 28, 29),
                ({PlayerId}, 0, 37, 38, 39);
            """);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void GetAccountRole_FreshReads_ObserveChangesWithoutCreatingMissingAccounts()
    {
        var manager = CreateManager();
        Assert.Equal(AccountRole.Admin, manager.GetAccountRole(AdminId));
        Assert.Equal(AccountRole.Moderator, manager.GetAccountRole(ModeratorId));
        Assert.Equal(AccountRole.NormalPlayer, manager.GetAccountRole(PlayerId));
        Assert.True(manager.TryGetAccountRole(MissingId, out var missingRole));
        Assert.Equal(AccountRole.NormalPlayer, missingRole);
        Assert.Equal(AccountRole.NormalPlayer, manager.GetAccountRole(MissingId));
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM `accounts` WHERE `account_id` = {MissingId}"));

        Execute($"UPDATE `accounts` SET `role` = 0 WHERE `account_id` = {AdminId}");

        Assert.Equal(AccountRole.NormalPlayer, manager.GetAccountRole(AdminId));
        Assert.Equal(AccountRole.NormalPlayer, manager.GetAccountDetails(AdminId).Role);
    }

    [Fact]
    public void GetAccountRole_DatabaseReadFails_DeniesActorAndTargetAuthority()
    {
        Execute("RENAME TABLE `accounts` TO `role_read_failure_accounts`");
        try
        {
            var manager = CreateManager();
            Assert.False(manager.TryGetAccountRole(AdminId, out var role));
            Assert.Equal(AccountRole.NormalPlayer, role);
            Assert.Equal(AccountRole.NormalPlayer, manager.GetAccountRole(AdminId));
        }
        finally
        {
            Execute("RENAME TABLE `role_read_failure_accounts` TO `accounts`");
        }
    }

    [Fact]
    public void GetAccountRole_InvalidStoredRole_DeniesActorAndTargetAuthority()
    {
        Execute("ALTER TABLE `accounts` ALTER CHECK `chk_accounts_role` NOT ENFORCED");
        try
        {
            Execute($"UPDATE `accounts` SET `role` = 255 WHERE `account_id` = {AdminId}");
            var manager = CreateManager();

            Assert.False(manager.TryGetAccountRole(AdminId, out var role));
            Assert.Equal(AccountRole.NormalPlayer, role);
            Assert.Equal(AccountRole.NormalPlayer, manager.GetAccountRole(AdminId));
            Assert.Equal(AccountRoleChangeStatus.Rejected, manager.SetAccountRole(AdminId, PlayerId, AccountRole.Admin).Status);
        }
        finally
        {
            Execute($"UPDATE `accounts` SET `role` = 2 WHERE `account_id` = {AdminId}");
            Execute("ALTER TABLE `accounts` ALTER CHECK `chk_accounts_role` ENFORCED");
        }
    }

    [Fact]
    public void SetAccountRole_Admin_CommitsOnlyTheRoleBeforeSuccess()
    {
        var result = CreateManager().SetAccountRole(AdminId, PlayerId, AccountRole.Moderator);
        Assert.Equal(AccountRoleChangeStatus.Completed, result.Status);
        Assert.Equal(string.Empty, result.Detail);

        var reloaded = CreateManager().GetAccountDetails(PlayerId);
        Assert.Equal((int)PlayerId, reloaded.AccountId);
        Assert.Equal(AccountRole.Moderator, reloaded.Role);
        Assert.Equal(37, reloaded.Labor);
        Assert.Equal(38, reloaded.Credits);
        Assert.Equal(39, reloaded.Loyalty);
    }

    [Theory]
    [InlineData(ModeratorId)]
    [InlineData(PlayerId)]
    [InlineData(MissingId)]
    public void SetAccountRole_NonAdmin_DoesNotChangeTarget(uint actorId)
    {
        var result = CreateManager().SetAccountRole(actorId, PlayerId, AccountRole.Admin);
        Assert.Equal(AccountRoleChangeStatus.Rejected, result.Status);
        Assert.Equal("Only an Admin can change account roles.", result.Detail);
        Assert.Equal(AccountRole.NormalPlayer, CreateManager().GetAccountRole(PlayerId));
    }

    [Fact]
    public void SetAccountRole_MissingTarget_DoesNotInsertAnAccount()
    {
        var result = CreateManager().SetAccountRole(AdminId, MissingId, AccountRole.Admin);
        Assert.Equal(AccountRoleChangeStatus.Rejected, result.Status);
        Assert.Equal("The target account does not exist in Game.", result.Detail);
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM `accounts` WHERE `account_id` = {MissingId}"));
    }

    [Fact]
    public void SetAccountRole_WriteFailure_PreservesRoleAndAllowsRetry()
    {
        Execute($"""
            CREATE TRIGGER `fail_account_role_update` BEFORE UPDATE ON `accounts`
            FOR EACH ROW BEGIN
                IF NEW.account_id = {PlayerId} THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected account role failure';
                END IF;
            END
            """);

        var result = CreateManager().SetAccountRole(AdminId, PlayerId, AccountRole.Admin);
        Assert.Equal(AccountRoleChangeStatus.Rejected, result.Status);
        Assert.Equal("The account role change failed before commit.", result.Detail);
        Assert.Equal(AccountRole.NormalPlayer, CreateManager().GetAccountRole(PlayerId));
        Execute("DROP TRIGGER `fail_account_role_update`");

        Assert.Equal(AccountRoleChangeStatus.Completed, CreateManager().SetAccountRole(AdminId, PlayerId, AccountRole.Admin).Status);
        Assert.Equal(AccountRole.Admin, CreateManager().GetAccountRole(PlayerId));
    }

    [Fact]
    public async Task SetAccountRole_ConcurrentAdminDemotions_RechecksLockedActorAuthority()
    {
        Execute($"UPDATE `accounts` SET `role` = 2 WHERE `account_id` = {ModeratorId}");
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var first = ChangeRole(AdminId, ModeratorId);
        var second = ChangeRole(ModeratorId, AdminId);
        var bothReady = ready.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();
        Assert.True(bothReady);

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == AccountRoleChangeStatus.Completed);
        Assert.Single(results, result => result.Status == AccountRoleChangeStatus.Rejected &&
                                        result.Detail == "Only an Admin can change account roles.");
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM `accounts` WHERE `account_id` IN ({AdminId}, {ModeratorId}) AND `role` = 2"));

        Task<AccountRoleChangeResult> ChangeRole(uint actor, uint target)
        {
            return Task.Run(() =>
            {
                var manager = CreateManager();
                Assert.Equal(AccountRole.Admin, manager.GetAccountRole(actor));
                ready.Signal();
                release.Wait();
                return manager.SetAccountRole(actor, target, AccountRole.NormalPlayer);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetAccountRole_CommitAcknowledgementFails_ReportsUnconfirmedWhetherOrNotWriteCommitted(bool commitReachedDatabase)
    {
        var result = CreateManager().SetAccountRole(AdminId, PlayerId, AccountRole.Moderator, transaction =>
        {
            if (commitReachedDatabase)
                transaction.Commit();
            throw new IOException("Injected loss of commit acknowledgement.");
        });

        Assert.Equal(AccountRoleChangeStatus.Unconfirmed, result.Status);
        Assert.Equal("The account role commit was not confirmed. Check the current role before a retry.", result.Detail);
        Assert.Equal(commitReachedDatabase ? AccountRole.Moderator : AccountRole.NormalPlayer,
            CreateManager().GetAccountRole(PlayerId));
    }

    [Fact]
    public void GetAccountDetails_FirstAccount_IsNormalAndKeepsExplicitIdAndDefaults()
    {
        var config = AppConfiguration.Instance;
        var oldLabor = config.Labor;
        var oldCredits = config.Credits;
        var oldLoyalty = config.Loyalty;
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMPORARY TABLE `saved_role_test_accounts` AS SELECT * FROM `accounts`; DELETE FROM `accounts`";
        command.ExecuteNonQuery();
        try
        {
            config.Labor = new CurrencyValuesConfig { Default = 50 };
            config.Credits = new CurrencyValuesConfig { Default = 7 };
            config.Loyalty = new CurrencyValuesConfig { Default = 9 };

            var created = CreateManager().GetAccountDetails(MissingId);
            var reloaded = CreateManager().GetAccountDetails(MissingId);

            Assert.Equal((int)MissingId, created.AccountId);
            Assert.Equal(AccountRole.NormalPlayer, created.Role);
            Assert.Equal(AccountRole.NormalPlayer, reloaded.Role);
            Assert.Equal(50, created.Labor);
            Assert.Equal(7, created.Credits);
            Assert.Equal(9, created.Loyalty);
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM `accounts` WHERE `account_id` = 0"));
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM `accounts`"));
        }
        finally
        {
            config.Labor = oldLabor;
            config.Credits = oldCredits;
            config.Loyalty = oldLoyalty;
            command.CommandText = "DELETE FROM `accounts`; INSERT INTO `accounts` SELECT * FROM `saved_role_test_accounts`; DROP TEMPORARY TABLE `saved_role_test_accounts`";
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Schema_RejectsUnknownRolesAndStaffRoleForAccountZero()
    {
        Assert.Throws<MySqlException>(() => Execute($"UPDATE `accounts` SET `role` = 3 WHERE `account_id` = {PlayerId}"));
        Assert.Throws<MySqlException>(() => Execute("INSERT INTO `accounts` (`account_id`, `role`) VALUES (0, 2) ON DUPLICATE KEY UPDATE `role` = 2"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'characters' AND COLUMN_NAME IN ('role', 'access_level')"));
    }

    [Fact]
    public void Migration_MapsAccountRolesAndPreservesBalances_AndDoesNotUndoLaterRoleChanges()
    {
        CreateLegacyTables();
        Execute("""
            INSERT INTO `role_test_accounts` (`account_id`, `access_level`, `credits`) VALUES
                (0, 100, 90), (11, -2, 91), (12, 0, 92), (13, 49, 93),
                (14, 50, 94), (15, 99, 95), (16, 100, 96), (17, 150, 97);
            INSERT INTO `role_test_characters` (`id`, `account_id`, `access_level`) VALUES
                (101, 16, 100), (102, 14, 50);
            """);

        ExecuteMigration();

        foreach (var (id, role) in new[] { (0, 0), (11, 0), (12, 0), (13, 0), (14, 1), (15, 1), (16, 2), (17, 2) })
            Assert.Equal(role, Scalar($"SELECT `role` FROM `role_test_accounts` WHERE `account_id` = {id}"));
        Assert.Equal(748L, Scalar("SELECT SUM(`credits`) FROM `role_test_accounts`"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('role_test_accounts', 'role_test_characters') AND COLUMN_NAME = 'access_level'"));

        Execute("UPDATE `role_test_accounts` SET `role` = 2 WHERE `account_id` = 14");
        ExecuteMigration();

        Assert.Equal(2L, Scalar("SELECT `role` FROM `role_test_accounts` WHERE `account_id` = 14"));
        Assert.Throws<MySqlException>(() => Execute("UPDATE `role_test_accounts` SET `role` = 3 WHERE `account_id` = 14"));
        Assert.Throws<MySqlException>(() => Execute("UPDATE `role_test_accounts` SET `role` = 2 WHERE `account_id` = 0"));
    }

    [Theory]
    [InlineData(11, 50, 11, 100)]
    [InlineData(11, 100, 12, 0)]
    [InlineData(0, 100, 0, 0)]
    public void Migration_CharacterOnlyAccessOrOrphan_StopsBeforeChangingSchema(
        int accountId, int accountLevel, int characterAccountId, int characterLevel)
    {
        CreateLegacyTables();
        Execute($"""
            INSERT INTO `role_test_accounts` (`account_id`, `access_level`) VALUES ({accountId}, {accountLevel});
            INSERT INTO `role_test_characters` (`id`, `account_id`, `access_level`) VALUES (101, {characterAccountId}, {characterLevel});
            """);

        var error = Assert.Throws<MySqlException>(ExecuteMigration);

        Assert.Contains("review orphan characters or character-only access", error.Message);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'role_test_accounts' AND COLUMN_NAME = 'role'"));
        Assert.Equal(accountLevel, Scalar($"SELECT `access_level` FROM `role_test_accounts` WHERE `account_id` = {accountId}"));
        Assert.Equal(characterLevel, Scalar("SELECT `access_level` FROM `role_test_characters` WHERE `id` = 101"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_RetryAfterDdlBoundary_UsesAccountSource(bool characterColumnRemoved)
    {
        CreateLegacyTables();
        Execute("""
            INSERT INTO `role_test_accounts` (`account_id`, `access_level`) VALUES (11, 100);
            ALTER TABLE `role_test_accounts` ADD COLUMN `role` TINYINT UNSIGNED NOT NULL DEFAULT 0;
            """);
        if (characterColumnRemoved)
            Execute("ALTER TABLE `role_test_characters` DROP COLUMN `access_level`");

        ExecuteMigration();

        Assert.Equal(2L, Scalar("SELECT `role` FROM `role_test_accounts` WHERE `account_id` = 11"));
    }

    private static AccountManager CreateManager()
    {
        return new AccountManager(Mock.Of<ITickManager>(), Mock.Of<ITimedRewardsManager>(), TimeProvider.System);
    }

    private static void CreateLegacyTables()
    {
        Execute("""
            CREATE TABLE `role_test_accounts` (
                `account_id` INT NOT NULL PRIMARY KEY,
                `access_level` INT NOT NULL DEFAULT 0,
                `credits` INT NOT NULL DEFAULT 0
            ) ENGINE=InnoDB;
            CREATE TABLE `role_test_characters` (
                `id` INT UNSIGNED NOT NULL PRIMARY KEY,
                `account_id` INT UNSIGNED NOT NULL,
                `access_level` INT UNSIGNED NOT NULL DEFAULT 0
            ) ENGINE=InnoDB;
            """);
    }

    private static void ExecuteMigration()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SQL", "updates", "2026-09-10_aaemu_game_account_roles.sql"));
        // The disposable tables keep migration failure cases separate from other GameMySql fixtures.
        sql = sql.Replace("`accounts`", "`role_test_accounts`", StringComparison.Ordinal)
            .Replace("'accounts'", "'role_test_accounts'", StringComparison.Ordinal)
            .Replace("`characters`", "`role_test_characters`", StringComparison.Ordinal)
            .Replace("'characters'", "'role_test_characters'", StringComparison.Ordinal)
            .Replace("chk_accounts_", "chk_role_test_accounts_", StringComparison.Ordinal);
        Execute(sql);
    }

    private static void Cleanup()
    {
        Execute($"""
            DROP TRIGGER IF EXISTS `fail_account_role_update`;
            DROP PROCEDURE IF EXISTS `migrate_account_roles_20260910`;
            DROP TABLE IF EXISTS `role_test_characters`;
            DROP TABLE IF EXISTS `role_test_accounts`;
            DELETE FROM `accounts` WHERE `account_id` IN ({AdminId}, {ModeratorId}, {PlayerId}, {MissingId});
            """);
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
