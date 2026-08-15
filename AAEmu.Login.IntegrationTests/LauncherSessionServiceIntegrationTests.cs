using AAEmu.Commons.Utils.DB;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Models;
using AAEmu.Login.Utils;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AAEmu.Login.IntegrationTests;

[Collection("MySql")]
public class LauncherSessionServiceIntegrationTests : IAsyncLifetime
{
    private static readonly AccountId s_accountId = new(1);
    private IMySqlConnectionFactory _connectionFactory = null!;
    private LauncherSessionService _service = null!;

    public async ValueTask InitializeAsync()
    {
        await using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET FOREIGN_KEY_CHECKS = 0;
            TRUNCATE TABLE launcher_launch_tickets;
            TRUNCATE TABLE launcher_sessions;
            TRUNCATE TABLE users;
            SET FOREIGN_KEY_CHECKS = 1;
            INSERT INTO users
                (id, username, password, email, last_ip, last_login, created_at, updated_at)
            VALUES
                (1, 'alice', 'unused', '', '', 0, 0, 0);
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var connectionFactory = new Mock<IMySqlConnectionFactory>();
        connectionFactory.Setup(factory => factory.CreateConnection()).Returns(MySQL.CreateConnection);
        _connectionFactory = connectionFactory.Object;
        _service = new LauncherSessionService(
            _connectionFactory,
            Options.Create(new LauncherApiOptions
            {
                AccessTokenLifetimeMinutes = 15,
                RefreshTokenLifetimeDays = 30
            }),
            TimeProvider.System);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SessionLifecycle_RotateThenRevoke_InvalidatesPreviousTokens()
    {
        var created = await _service.CreateAsync(s_accountId, "alice", TestContext.Current.CancellationToken);
        var authenticated = await _service.AuthenticateAccessTokenAsync(
            created.AccessToken, TestContext.Current.CancellationToken);
        var refreshed = await _service.RefreshAsync(
            created.RefreshToken, TestContext.Current.CancellationToken);
        var oldAccess = await _service.AuthenticateAccessTokenAsync(
            created.AccessToken, TestContext.Current.CancellationToken);
        var oldRefresh = await _service.RefreshAsync(
            created.RefreshToken, TestContext.Current.CancellationToken);

        Assert.NotNull(authenticated);
        Assert.NotNull(refreshed);
        Assert.Null(oldAccess);
        Assert.Null(oldRefresh);

        await _service.RevokeAsync(refreshed!.Principal.SessionId, TestContext.Current.CancellationToken);
        var revoked = await _service.AuthenticateAccessTokenAsync(
            refreshed.AccessToken, TestContext.Current.CancellationToken);
        Assert.Null(revoked);
    }

    [Fact]
    public async Task LaunchTicket_IssuedByOneService_IsConsumedOnceByAnother()
    {
        var session = await _service.CreateAsync(
            s_accountId, "alice", TestContext.Current.CancellationToken);
        var options = Options.Create(new LauncherApiOptions { Enabled = true, LaunchTicketLifetimeSeconds = 60 });
        var issuer = new LaunchTicketService(
            _service,
            new MySqlLaunchTicketStore(_connectionFactory, TimeProvider.System),
            options,
            TimeProvider.System);
        var consumer = new LaunchTicketService(
            _service,
            new MySqlLaunchTicketStore(_connectionFactory, TimeProvider.System),
            options,
            TimeProvider.System);

        var issued = await issuer.IssueAsync(session.Principal, TestContext.Current.CancellationToken);
        var first = await consumer.ConsumeAsync(
            "alice", issued!.Ticket, TestContext.Current.CancellationToken);
        var second = await consumer.ConsumeAsync(
            "alice", issued.Ticket, TestContext.Current.CancellationToken);

        Assert.Equal(new ConsumedLaunchTicket(s_accountId, "alice"), first);
        Assert.Null(second);
    }
}
