using System.Net;
using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;
using Xunit;

namespace AAEmu.IntegrationTests.Fixtures;

public sealed class GameMySqlFixture : IAsyncLifetime
{
    public const string LocalConnectionEnvironmentVariable = "AAEMU_GAME_TEST_MYSQL_CONNECTION";
    private MySqlContainer _container;
    private string _connectionString;
    private string _localSchema;
    private string _localAdminConnectionString;

    public async ValueTask InitializeAsync()
    {
        var local = Environment.GetEnvironmentVariable(LocalConnectionEnvironmentVariable);
        if (string.IsNullOrEmpty(local))
        {
            // Docker remains the CI default. No container is constructed for a local override.
            _container = new MySqlBuilder("mysql:8.0")
                .WithCommand("--log-bin-trust-function-creators=1")
                .WithDatabase("aaemu_game")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        else
        {
            var builder = ParseLocalConnection(local);
            _localAdminConnectionString = builder.ConnectionString;
            _localSchema = $"aaemu_game_test_{Guid.NewGuid():N}";
            await using var admin = new MySqlConnection(_localAdminConnectionString);
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE `{_localSchema}` DEFAULT CHARACTER SET utf8mb4";
            await create.ExecuteNonQueryAsync();
            builder.Database = _localSchema;
            _connectionString = builder.ConnectionString;
        }

        try
        {
            await InitializeSchemaAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    internal static MySqlConnectionStringBuilder ParseLocalConnection(string value)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(value);
        }
        catch
        {
            // A parse exception can contain credentials. Do not include its text or value.
            throw new InvalidOperationException($"{LocalConnectionEnvironmentVariable} is not a valid MySQL connection string.");
        }

        if (!IPAddress.TryParse(builder.Server, out var address) || !IPAddress.IsLoopback(address) ||
            builder.Database.Length != 0 || builder.ConnectionProtocol != MySqlConnectionProtocol.Tcp)
            throw new InvalidOperationException($"{LocalConnectionEnvironmentVariable} must use a loopback IP, TCP, and no database name. The fixture creates its own disposable schema.");

        return builder;
    }

    private async Task InitializeSchemaAsync()
    {

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "SQL", "aaemu_game.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);
        var filteredLines = schemaSql
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.TrimStart().ToUpperInvariant();
                return !trimmed.StartsWith("CREATE DATABASE") && !trimmed.StartsWith("USE ");
            });

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = string.Join('\n', filteredLines);
        await command.ExecuteNonQueryAsync();

        foreach (var table in new[] { "account_daily_login_claims", "auction_mail_claims" })
        {
            command.CommandText = $"DROP TABLE `{table}`";
            await command.ExecuteNonQueryAsync();
            var updatePath = Path.Combine(
                AppContext.BaseDirectory, "SQL", "updates", $"2026-09-01_aaemu_game_{table}.sql");
            command.CommandText = await File.ReadAllTextAsync(updatePath);
            await command.ExecuteNonQueryAsync();
            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = "DROP TABLE `zone_conflict_states`";
        await command.ExecuteNonQueryAsync();
        var zoneUpdatePath = Path.Combine(AppContext.BaseDirectory, "SQL", "updates",
            "2026-09-07_aaemu_game_zone_conflict_states.sql");
        command.CommandText = await File.ReadAllTextAsync(zoneUpdatePath);
        await command.ExecuteNonQueryAsync();
        await command.ExecuteNonQueryAsync();

        // Exercise the mail update from a pre-update schema before any test creates assets.
        command.CommandText = "DROP TABLE `mail_archive_items`; DROP TABLE `mail_lifecycle`";
        await command.ExecuteNonQueryAsync();
        var mailUpdatePath = Path.Combine(AppContext.BaseDirectory, "SQL", "updates",
            "2026-09-11_aaemu_game_mail_lifecycle.sql");
        command.CommandText = await File.ReadAllTextAsync(mailUpdatePath);
        await command.ExecuteNonQueryAsync();
        await command.ExecuteNonQueryAsync();

        var builder = new MySqlConnectionStringBuilder(_connectionString);
        MySQL.SetConfiguration(new MySqlConnectionSettings
        {
            Host = builder.Server,
            Port = (ushort)builder.Port,
            User = builder.UserID,
            Password = builder.Password,
            Database = builder.Database
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_localSchema != null)
        {
            // This exact identifier is generated by this fixture, never read from the environment.
            await using var admin = new MySqlConnection(_localAdminConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{_localSchema}`";
            await drop.ExecuteNonQueryAsync();
            _localSchema = null;
        }
        if (_container != null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition("GameMySql", DisableParallelization = true)]
public sealed class GameMySqlCollection : ICollectionFixture<GameMySqlFixture>;
