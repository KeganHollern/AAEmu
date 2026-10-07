using System.Numerics;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Units;
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
        var player = CreateSavedPlayer(Owner);
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
        var bad = CreateSavedPlayer(Owner);
        var good = CreateSavedPlayer(Owner + 1);
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
    public void UiSave_DeletedStaleCharacterCannotRestoreOptionsOrBlockValidBatchMember()
    {
        using var connection = MySQL.CreateConnection();
        _ = CreateSavedPlayer(Owner);
        var stale = new Character(null) { Id = Owner, AccountId = Owner };
        var good = CreateSavedPlayer(Owner + 1);
        stale.SetOption(5, "old layout");
        stale.SaveOption(5);
        good.SetOption(5, "active layout");
        good.SaveOption(5);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE characters SET deleted=1,name='!Ui{Owner}' WHERE id={Owner}";
        command.ExecuteNonQuery();
        Assert.False(stale.IsDeleted);
        lock (SaveManager.PersistenceSyncRoot)
            UiDataSaveManager.SaveBatch([stale, good]);
        Assert.True(stale.IsDeleted);
        Assert.False(stale.HasPendingUiData);
        Assert.False(good.HasPendingUiData);
        Assert.Equal(0, CountOptions());
        command.CommandText = $"SELECT value FROM options WHERE owner={Owner + 1} AND `key`=5";
        Assert.Equal("active layout", command.ExecuteScalar());
        command.CommandText = $"SELECT deleted FROM characters WHERE id={Owner} AND name='!Ui{Owner}'";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        using var transaction = connection.BeginTransaction();
        stale.SaveUiOptions(connection, transaction, context: null, onlyDirty: false);
        transaction.Commit();
        Assert.Equal(0, CountOptions());
    }

    [Fact]
    public void UiSave_MissingCharacterCannotCreateOrphanOptionsAndCanRetryAfterCreation()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM options WHERE owner={Owner}; DELETE FROM characters WHERE id={Owner}";
        command.ExecuteNonQuery();
        var missing = new Character(null) { Id = Owner, AccountId = Owner };
        var good = CreateSavedPlayer(Owner + 1);
        missing.SetOption(5, "pending owner");
        missing.SaveOption(5);
        good.SetOption(5, "valid owner");
        good.SaveOption(5);
        lock (SaveManager.PersistenceSyncRoot)
            UiDataSaveManager.SaveBatch([missing, good]);
        Assert.False(missing.IsDeleted);
        Assert.True(missing.HasPendingUiData);
        Assert.False(good.HasPendingUiData);
        Assert.Equal(0, CountOptions());
        command.CommandText = $"SELECT value FROM options WHERE owner={Owner + 1} AND `key`=5";
        Assert.Equal("valid owner", command.ExecuteScalar());
        _ = CreateSavedPlayer(Owner);
        lock (SaveManager.PersistenceSyncRoot)
            UiDataSaveManager.SaveBatch([missing]);
        Assert.False(missing.HasPendingUiData);
        Assert.Equal(1, CountOptions());
    }

    [Fact]
    public void UiSave_WrongAccountCannotWriteAnotherCharactersOptions()
    {
        _ = CreateSavedPlayer(Owner);
        var wrongAccount = new Character(null) { Id = Owner, AccountId = Owner + 1 };
        wrongAccount.SetOption(5, "wrong owner");
        wrongAccount.SaveOption(5);
        lock (SaveManager.PersistenceSyncRoot)
            UiDataSaveManager.SaveBatch([wrongAccount]);
        Assert.Equal(0, CountOptions());
        Assert.True(wrongAccount.HasPendingUiData);
    }

    [Fact]
    public void UiSave_InitialFullCharacterSaveCreatesOwnerBeforeOptions()
    {
        const uint id = Owner + 2;
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM options WHERE owner={id}; DELETE FROM characters WHERE id={id}";
        command.ExecuteNonQuery();
        var player = new Character(new UnitCustomModelParams())
        {
            Id = id, AccountId = id, Name = $"Ui{id}", Faction = new SystemFaction(), FactionName = "",
            Slots = [], Created = DateTime.UtcNow
        };
        player.SetOption(5, "initial layout");
        player.SaveOption(5);
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var transaction = connection.BeginTransaction();
            var context = new PersistenceSaveContext(connection, transaction);
            Assert.True(player.Save(context));
            transaction.Commit();
            context.AcknowledgeCommit();
        }
        Assert.False(player.HasPendingUiData);
        command.CommandText = $"SELECT value FROM options WHERE owner={id} AND `key`=5";
        Assert.Equal("initial layout", command.ExecuteScalar());
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

    private static Character CreateSavedPlayer(uint id)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM options WHERE owner={id};
            DELETE FROM characters WHERE id={id};
            INSERT INTO characters
                (id,account_id,name,race,gender,unit_model_params,level,experience,recoverable_exp,hp,mp,
                 consumed_lp,ability1,ability2,ability3,world_id,zone_id,x,y,z,faction_id,faction_name,
                 expedition_id,family,dead_count,rez_wait_duration,rez_penalty_duration,money,
                 auto_use_aapoint,prev_point,point,gift,expanded_expert,slots)
            VALUES({id},{id},'Ui{id}',1,1,X'',1,0,0,100,100,0,1,11,11,1,1,0,0,0,1,'',
                   0,0,0,0,0,0,0,0,0,0,0,X'');
            """;
        command.ExecuteNonQuery();
        return new Character(null) { Id = id, AccountId = id };
    }

    private static long CountOptions()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM options WHERE owner={Owner}";
        return (long)command.ExecuteScalar();
    }
}
