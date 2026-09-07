using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;
using Xunit;

namespace AAEmu.IntegrationTests.Fixtures;

public sealed class GameMySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.0")
        .WithDatabase("aaemu_game")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "SQL", "aaemu_game.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);
        var filteredLines = schemaSql
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.TrimStart().ToUpperInvariant();
                return !trimmed.StartsWith("CREATE DATABASE") && !trimmed.StartsWith("USE ");
            });

        await using var connection = new MySqlConnection(_container.GetConnectionString());
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

        var builder = new MySqlConnectionStringBuilder(_container.GetConnectionString());
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
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("GameMySql", DisableParallelization = true)]
public sealed class GameMySqlCollection : ICollectionFixture<GameMySqlFixture>;
