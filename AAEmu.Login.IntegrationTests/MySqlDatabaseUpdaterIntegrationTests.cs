using AAEmu.Commons.IO;
using AAEmu.Commons.Utils.DB;
using AAEmu.Commons.Utils.Updater;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.Login.IntegrationTests;

[Collection("MySql")]
public sealed class MySqlDatabaseUpdaterIntegrationTests : IAsyncLifetime
{
    private readonly List<string> _updateFiles = [];

    public async ValueTask InitializeAsync()
    {
        await ResetDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var updateFile in _updateFiles)
            File.Delete(updateFile);

        await ResetDatabaseAsync();
    }

    [Fact]
    public async Task Run_AutoApply_AppliesFilesInOrderAndDoesNotRepeatThem()
    {
        const string moduleName = "aaemu_updater_success";
        WriteUpdateFile("2026-08-30_aaemu_updater_success_01.sql", """
            CREATE TABLE updater_success (`id` INT NOT NULL PRIMARY KEY, `step` INT NOT NULL);
            INSERT INTO updater_success (`id`, `step`) VALUES (1, 1);
            """);
        WriteUpdateFile("2026-08-30_aaemu_updater_success_02.sql", """
            UPDATE updater_success SET `step` = 2 WHERE `id` = 1 AND `step` = 1;
            """);

        using var connection = MySQL.CreateConnection();
        var firstResult = MySqlDatabaseUpdater.Run(connection, moduleName, connection.Database, autoApply: true);
        var secondResult = MySqlDatabaseUpdater.Run(connection, moduleName, connection.Database, autoApply: true);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.Equal(2, Convert.ToInt32(await ExecuteScalarAsync(connection,
            "SELECT `step` FROM updater_success WHERE `id` = 1")));
        Assert.Equal(2, Convert.ToInt32(await ExecuteScalarAsync(connection,
            "SELECT COUNT(*) FROM updates WHERE script_name LIKE '%aaemu_updater_success%' AND installed = 1")));
    }

    [Fact]
    public async Task Run_FailedFile_RecordsAttemptAndStopsBeforeLaterFiles()
    {
        const string moduleName = "aaemu_updater_failure";
        const string failedFileName = "2026-08-30_aaemu_updater_failure_02.sql";
        WriteUpdateFile("2026-08-30_aaemu_updater_failure_01.sql", """
            CREATE TABLE updater_failure (`step` VARCHAR(32) NOT NULL PRIMARY KEY);
            INSERT INTO updater_failure (`step`) VALUES ('first');
            """);
        WriteUpdateFile(failedFileName, """
            INSERT INTO updater_missing_table (`id`) VALUES (1);
            """);
        WriteUpdateFile("2026-08-30_aaemu_updater_failure_03.sql", """
            INSERT INTO updater_failure (`step`) VALUES ('later');
            """);

        using var connection = MySQL.CreateConnection();
        var beforeAttempt = DateTime.UtcNow;
        var result = MySqlDatabaseUpdater.Run(connection, moduleName, connection.Database, autoApply: true);
        var afterAttempt = DateTime.UtcNow;

        Assert.False(result);
        Assert.Equal("first", Convert.ToString(await ExecuteScalarAsync(connection,
            "SELECT GROUP_CONCAT(`step` ORDER BY `step`) FROM updater_failure")));
        Assert.Equal(2, Convert.ToInt32(await ExecuteScalarAsync(connection,
            "SELECT COUNT(*) FROM updates WHERE script_name LIKE '%aaemu_updater_failure%'")));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT installed, install_date, last_error FROM updates WHERE script_name = @script_name";
        command.Parameters.AddWithValue("@script_name", failedFileName);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, reader.GetInt32(0));
        Assert.InRange(reader.GetDateTime(1), beforeAttempt.AddSeconds(-1), afterAttempt.AddSeconds(1));
        Assert.NotEmpty(reader.GetString(2));
    }

    [Fact]
    public async Task Run_PendingFileWithoutInteractiveInput_ReturnsFalseWithoutInstallingIt()
    {
        const string moduleName = "aaemu_updater_unattended";
        WriteUpdateFile("2026-08-30_aaemu_updater_unattended.sql", """
            CREATE TABLE updater_unattended (`id` INT NOT NULL PRIMARY KEY);
            """);

        using var connection = MySQL.CreateConnection();
        var result = MySqlDatabaseUpdater.RunCore(connection, moduleName, connection.Database,
            autoApply: false, canPrompt: false);

        Assert.False(result);
        Assert.Equal(0, Convert.ToInt32(await ExecuteScalarAsync(connection, """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = 'updater_unattended'
            """)));
        Assert.Equal(0, Convert.ToInt32(await ExecuteScalarAsync(connection,
            "SELECT COUNT(*) FROM updates WHERE script_name LIKE '%aaemu_updater_unattended%'")));
    }

    private void WriteUpdateFile(string fileName, string sql)
    {
        var updatesDirectory = Path.Combine(FileManager.AppPath, "SQL", "updates");
        Directory.CreateDirectory(updatesDirectory);
        var updateFile = Path.Combine(updatesDirectory, fileName);
        File.WriteAllText(updateFile, sql);
        _updateFiles.Add(updateFile);
    }

    private static async Task<object?> ExecuteScalarAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ResetDatabaseAsync()
    {
        using var connection = MySQL.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS updater_success;
            DROP TABLE IF EXISTS updater_failure;
            DROP TABLE IF EXISTS updater_unattended;
            DROP TABLE IF EXISTS updates;
            CREATE TABLE updates (
                script_name VARCHAR(255) NOT NULL PRIMARY KEY,
                installed TINYINT NOT NULL DEFAULT 0,
                install_date DATETIME NOT NULL,
                last_error TEXT NOT NULL
            ) COLLATE 'utf8mb4_general_ci';
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
