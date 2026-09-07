using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;

using Moq;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class DivineClockResetTests
{
    private const uint AccountId = 901;

    [Fact]
    public async Task AddDivineClockTime_WaitsForDailyReset_DoesNotRestorePreviousDayState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var resetConnection = MySQL.CreateConnection();
        await using var resetCommand = resetConnection.CreateCommand();
        resetCommand.CommandText = """
            INSERT INTO `accounts` (`account_id`, `divine_clock_time`, `divine_clock_taken`)
            VALUES (901, 60, 2)
            ON DUPLICATE KEY UPDATE `divine_clock_time` = 60, `divine_clock_taken` = 2
            """;
        await resetCommand.ExecuteNonQueryAsync(cancellationToken);

        // Hold the row lock after the daily claim's reset while its transaction is still uncommitted.
        // A plain SELECT from another connection still sees yesterday's (60, 2) here.
        await using var resetTransaction = resetConnection.BeginTransaction();
        resetCommand.Transaction = resetTransaction;
        resetCommand.CommandText = """
            UPDATE `accounts`
            SET `divine_clock_time` = 0, `divine_clock_taken` = 0
            WHERE `account_id` = 901
            """;
        await resetCommand.ExecuteNonQueryAsync(cancellationToken);

        var manager = new AccountManager(
            Mock.Of<ITickManager>(),
            Mock.Of<ITimedRewardsManager>(),
            TimeProvider.System);
        var increment = Task.Run(() => manager.AddDivineClockTime(AccountId, 5), cancellationToken);
        try
        {
            await WaitForPendingClockUpdateAsync(cancellationToken);
        }
        finally
        {
            await resetTransaction.CommitAsync(cancellationToken);
            await increment;
        }

        var (elapsed, taken) = manager.GetDivineClock(AccountId);
        Assert.Equal(5u, elapsed);
        Assert.Equal(0u, taken);
    }

    private static async Task WaitForPendingClockUpdateAsync(CancellationToken cancellationToken)
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW FULL PROCESSLIST";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var query = reader["Info"] as string;
                    if (query != null &&
                        query.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                        query.Contains("divine_clock_time", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.Fail("The online clock update did not reach the account row held by the daily reset.");
    }
}
