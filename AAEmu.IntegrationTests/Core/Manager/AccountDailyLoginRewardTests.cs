using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;

using Moq;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
public sealed class AccountDailyLoginRewardTests : IAsyncLifetime
{
    private const uint FirstAccountId = 1;
    private const uint SecondAccountId = 2;
    private const int DailyCredits = 3;
    private const int DailyLoyalty = 5;

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsync("""
            DROP TRIGGER IF EXISTS `fail_daily_login_reward_update`;
            TRUNCATE TABLE `account_daily_login_claims`;
            TRUNCATE TABLE `accounts`;
            INSERT INTO `accounts`
                (`account_id`, `credits`, `loyalty`, `last_login`, `divine_clock_time`, `divine_clock_taken`)
            VALUES
                (1, 10, 20, '2026-08-30 12:00:00', 60, 2),
                (2, 30, 40, '2026-08-30 12:00:00', 90, 3);
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync("DROP TRIGGER IF EXISTS `fail_daily_login_reward_update`;");
    }

    [Fact]
    public async Task Migration_SeedsExistingAccountsOnceWithoutChangingBalances()
    {
        var cutoverDate = new DateOnly(2026, 8, 31);
        await ExecuteAsync("DROP TABLE `account_daily_login_claims`;");
        var updateSql = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "SQL",
                "updates",
                "2026-09-01_aaemu_game_account_daily_login_claims.sql"),
            TestContext.Current.CancellationToken);
        var frozenUpdateSql = $"SET timestamp = 1788177600;{Environment.NewLine}{updateSql}";

        await ExecuteAsync(frozenUpdateSql);
        await ExecuteAsync(frozenUpdateSql);

        var claimAttempt = CreateManager().TryClaimDailyLoginReward(
            FirstAccountId, cutoverDate, DailyCredits, DailyLoyalty);
        var firstAccountState = await ReadAccountStateAsync(FirstAccountId);
        var secondAccountState = await ReadAccountStateAsync(SecondAccountId);
        Assert.False(claimAttempt);
        Assert.Equal(10, firstAccountState.Credits);
        Assert.Equal(20, firstAccountState.Loyalty);
        Assert.Equal(60u, firstAccountState.DivineClockTime);
        Assert.Equal(2u, firstAccountState.DivineClockTaken);
        Assert.Equal(30, secondAccountState.Credits);
        Assert.Equal(40, secondAccountState.Loyalty);
        Assert.Equal(1, await CountClaimsAsync(FirstAccountId));
        Assert.Equal(1, await CountClaimsAsync(SecondAccountId));
        Assert.Equal(2, await CountCutoverClaimsAsync(cutoverDate));
    }

    [Fact]
    public async Task OnlineAtMidnight_ConcurrentResetAndReconnect_GrantsNewDayOnce()
    {
        var previousDay = new DateOnly(2026, 8, 30);
        var newDay = previousDay.AddDays(1);
        var loginManager = CreateManager();
        var resetManager = CreateManager();
        Assert.True(loginManager.TryClaimDailyLoginReward(
            FirstAccountId, previousDay, DailyCredits, DailyLoyalty));

        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var loginClaim = StartClaim(loginManager, FirstAccountId, newDay, ready, release);
        var resetClaim = StartClaim(resetManager, FirstAccountId, newDay, ready, release);
        var bothReady = ready.Wait(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        release.Set();
        Assert.True(bothReady);

        var results = await Task.WhenAll(loginClaim, resetClaim);
        var state = await ReadAccountStateAsync(FirstAccountId);

        Assert.Single(results, result => result);
        Assert.Equal(16, state.Credits);
        Assert.Equal(30, state.Loyalty);
        Assert.Equal(0u, state.DivineClockTime);
        Assert.Equal(0u, state.DivineClockTaken);
        Assert.Equal(2, await CountClaimsAsync(FirstAccountId));
    }

    [Fact]
    public async Task Reconnect_AfterCommittedClaim_DoesNotGrantAgain()
    {
        var firstProcessManager = CreateManager();
        var retryProcessManager = CreateManager();
        var rewardDate = new DateOnly(2026, 8, 31);

        var first = firstProcessManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var retryAfterLostAcknowledgement = retryProcessManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var nextDay = retryProcessManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate.AddDays(1), DailyCredits, DailyLoyalty);
        var state = await ReadAccountStateAsync(FirstAccountId);

        Assert.True(first);
        Assert.False(retryAfterLostAcknowledgement);
        Assert.True(nextDay);
        Assert.Equal(16, state.Credits);
        Assert.Equal(30, state.Loyalty);
        Assert.Equal(2, await CountClaimsAsync(FirstAccountId));
    }

    [Fact]
    public async Task FailedTransaction_RetrySameDay_GrantsExactlyOnce()
    {
        var failedProcessManager = CreateManager();
        var rewardDate = new DateOnly(2026, 8, 31);
        await ExecuteAsync("""
            CREATE TRIGGER `fail_daily_login_reward_update`
            BEFORE UPDATE ON `accounts`
            FOR EACH ROW
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated reward failure';
            """);

        var failed = failedProcessManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var stateAfterFailure = await ReadAccountStateAsync(FirstAccountId);
        var claimsAfterFailure = await CountClaimsAsync(FirstAccountId);
        await ExecuteAsync("DROP TRIGGER `fail_daily_login_reward_update`;");

        var retryProcessManager = CreateManager();
        var retried = retryProcessManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var stateAfterRetry = await ReadAccountStateAsync(FirstAccountId);

        Assert.False(failed);
        Assert.Equal(10, stateAfterFailure.Credits);
        Assert.Equal(20, stateAfterFailure.Loyalty);
        Assert.Equal(60u, stateAfterFailure.DivineClockTime);
        Assert.Equal(2u, stateAfterFailure.DivineClockTaken);
        Assert.Equal(0, claimsAfterFailure);
        Assert.True(retried);
        Assert.Equal(13, stateAfterRetry.Credits);
        Assert.Equal(25, stateAfterRetry.Loyalty);
        Assert.Equal(1, await CountClaimsAsync(FirstAccountId));
    }

    [Fact]
    public async Task MultipleCharactersOnAccount_ShareOneAccountDayClaim()
    {
        var firstCharacterManager = CreateManager();
        var secondCharacterManager = CreateManager();
        var otherAccountManager = CreateManager();
        var rewardDate = new DateOnly(2026, 8, 31);

        var firstCharacter = firstCharacterManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var secondCharacter = secondCharacterManager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var otherAccount = otherAccountManager.TryClaimDailyLoginReward(
            SecondAccountId, rewardDate, DailyCredits, DailyLoyalty);
        var firstAccountState = await ReadAccountStateAsync(FirstAccountId);
        var secondAccountState = await ReadAccountStateAsync(SecondAccountId);

        Assert.True(firstCharacter);
        Assert.False(secondCharacter);
        Assert.True(otherAccount);
        Assert.Equal(13, firstAccountState.Credits);
        Assert.Equal(25, firstAccountState.Loyalty);
        Assert.Equal(33, secondAccountState.Credits);
        Assert.Equal(45, secondAccountState.Loyalty);
        Assert.Equal(1, await CountClaimsAsync(FirstAccountId));
        Assert.Equal(1, await CountClaimsAsync(SecondAccountId));
    }

    [Fact]
    public async Task NegativeConfiguredAmounts_DoNotDebitAccount()
    {
        var manager = CreateManager();
        var rewardDate = new DateOnly(2026, 8, 31);

        var claimed = manager.TryClaimDailyLoginReward(
            FirstAccountId, rewardDate, -DailyCredits, -DailyLoyalty);
        var state = await ReadAccountStateAsync(FirstAccountId);

        Assert.True(claimed);
        Assert.Equal(10, state.Credits);
        Assert.Equal(20, state.Loyalty);
        Assert.Equal(0u, state.DivineClockTime);
        Assert.Equal(0u, state.DivineClockTaken);
        Assert.Equal(1, await CountClaimsAsync(FirstAccountId));
    }

    [Fact]
    public void Add_OverlappingAndRemovedReconnects_AlwaysAttemptDurableClaim()
    {
        var timedRewards = new Mock<ITimedRewardsManager>();
        var rewardDate = new DateOnly(2026, 8, 31);
        var manager = CreateManager(
            timedRewards.Object,
            new FixedTimeProvider(new DateTimeOffset(
                rewardDate,
                new TimeOnly(23, 59),
                TimeSpan.Zero)));

        manager.Add(new GameConnection(null) { AccountId = FirstAccountId });
        manager.Add(new GameConnection(null) { AccountId = FirstAccountId });
        manager.Remove(FirstAccountId);
        manager.Add(new GameConnection(null) { AccountId = FirstAccountId });

        timedRewards.Verify(
            rewards => rewards.DoDailyAccountLogin(FirstAccountId, rewardDate),
            Times.Exactly(3));
    }

    private static AccountManager CreateManager(
        ITimedRewardsManager timedRewardsManager = null,
        TimeProvider timeProvider = null)
    {
        return new AccountManager(
            Mock.Of<ITickManager>(),
            timedRewardsManager ?? Mock.Of<ITimedRewardsManager>(),
            timeProvider ?? TimeProvider.System);
    }

    private static Task<bool> StartClaim(
        AccountManager manager,
        uint accountId,
        DateOnly rewardDate,
        CountdownEvent ready,
        ManualResetEventSlim release)
    {
        return Task.Run(() =>
        {
            ready.Signal();
            release.Wait();
            return manager.TryClaimDailyLoginReward(
                accountId,
                rewardDate,
                DailyCredits,
                DailyLoyalty);
        });
    }

    private static async Task<AccountState> ReadAccountStateAsync(uint accountId)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT `credits`, `loyalty`, `divine_clock_time`, `divine_clock_taken`
            FROM `accounts`
            WHERE `account_id` = @account_id
            """;
        command.Parameters.AddWithValue("@account_id", accountId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new AccountState(
            Convert.ToInt32(reader["credits"]),
            Convert.ToInt32(reader["loyalty"]),
            Convert.ToUInt32(reader["divine_clock_time"]),
            Convert.ToUInt32(reader["divine_clock_taken"]));
    }

    private static async Task<int> CountClaimsAsync(uint accountId)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM `account_daily_login_claims` WHERE `account_id` = @account_id";
        command.Parameters.AddWithValue("@account_id", accountId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<int> CountCutoverClaimsAsync(DateOnly rewardDate)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM `account_daily_login_claims`
            WHERE `reward_date` = @reward_date
              AND `credits_amount` = 0
              AND `loyalty_amount` = 0
            """;
        command.Parameters.AddWithValue(
            "@reward_date",
            rewardDate.ToDateTime(TimeOnly.MinValue));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed record AccountState(
        int Credits,
        int Loyalty,
        uint DivineClockTime,
        uint DivineClockTaken);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
