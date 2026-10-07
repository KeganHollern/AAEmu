using System.Numerics;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.IntegrationTests.Fixtures;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class GameUiAuditPersistenceTests(GameMySqlFixture fixture)
{
    private readonly GameMySqlFixture _fixture = fixture;
    private const uint Owner = 941431;

    [Fact]
    public void UiSave_CoalescesRowsAndAcknowledgesOnlyAfterCommit()
    {
        using var connection = MySQL.CreateConnection();
        using var cleanup = connection.CreateCommand();
        cleanup.CommandText = $"DELETE FROM options WHERE owner={Owner}";
        cleanup.ExecuteNonQuery();
        var player = new Character(null) { Id = Owner };
        for (var index = 0; index < 100; ++index)
        {
            player.SetOption(5, index.ToString());
            player.SaveOption(5);
        }
        player.SetOption(1, "layout");
        player.SaveOption(1);
        using (var transaction = connection.BeginTransaction())
        {
            var context = new PersistenceSaveContext(connection, transaction);
            player.SaveUiOptions(connection, transaction, context, onlyDirty: true);
            Assert.True(player.HasPendingUiData);
            transaction.Rollback();
        }
        Assert.True(player.HasPendingUiData);
        Assert.Equal(0, CountOptions());
        using (var transaction = connection.BeginTransaction())
        {
            var context = new PersistenceSaveContext(connection, transaction);
            player.SaveUiOptions(connection, transaction, context, onlyDirty: true);
            transaction.Commit();
            context.AcknowledgeCommit();
        }
        Assert.False(player.HasPendingUiData);
        Assert.Equal(2, CountOptions());
        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT value FROM options WHERE owner={Owner} AND `key`=5";
        Assert.Equal("99", read.ExecuteScalar());
        player.SetOption(5, "logout checkpoint");
        using (var transaction = connection.BeginTransaction())
        {
            player.SaveUiOptions(connection, transaction, context: null, onlyDirty: false);
            transaction.Commit();
        }
        Assert.Equal("logout checkpoint", read.ExecuteScalar());
    }

    [Fact]
    public void UiSave_UnsupportedDocumentDoesNotBlockOtherCharacters()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM options WHERE owner IN ({Owner},{Owner + 1})";
        command.ExecuteNonQuery();
        var bad = new Character(null) { Id = Owner };
        var good = new Character(null) { Id = Owner + 1 };
        bad.SetOption(5, "unsupported \U0001f600");
        bad.SaveOption(5);
        good.SetOption(5, "valid");
        good.SaveOption(5);
        lock (SaveManager.PersistenceSyncRoot)
            UiDataSaveManager.SaveBatch([bad, good]);
        Assert.True(bad.HasPendingUiData);
        Assert.False(good.HasPendingUiData);
        Assert.Equal(0, CountOptions());
        command.CommandText = $"SELECT value FROM options WHERE owner={Owner + 1} AND `key`=5";
        Assert.Equal("valid", command.ExecuteScalar());
        bad.SetOption(5, "corrected");
        lock (SaveManager.PersistenceSyncRoot)
            UiDataSaveManager.SaveBatch([bad]);
        Assert.False(bad.HasPendingUiData);
        Assert.Equal(1, CountOptions());
    }

    [Fact]
    public void AuditSave_BuffersParameterizedRowsAndShutdownWritesFinalBatch()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM audit_char_sus WHERE sus_character={Owner}";
        command.ExecuteNonQuery();
        using var manager = new SusManager(null);
        for (var index = 0; index < SusManager.BatchSize + 1; ++index)
            Assert.True(manager.LogActivity("Cheat", 9, Owner, 10, new Vector3(1, 2, 3), $"quoted ' row {index}"));
        command.CommandText = $"SELECT COUNT(*) FROM audit_char_sus WHERE sus_character={Owner}";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.True(manager.FlushPending());
        Assert.Equal((long)SusManager.BatchSize, command.ExecuteScalar());
        Assert.Equal(1, manager.PendingCount);
        manager.Dispose();
        Assert.Equal((long)SusManager.BatchSize + 1, command.ExecuteScalar());
        command.CommandText = $"SELECT description FROM audit_char_sus WHERE sus_character={Owner} ORDER BY id DESC LIMIT 1";
        Assert.Equal($"quoted ' row {SusManager.BatchSize}", command.ExecuteScalar());
    }

    private static long CountOptions()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM options WHERE owner={Owner}";
        return (long)command.ExecuteScalar();
    }
}
