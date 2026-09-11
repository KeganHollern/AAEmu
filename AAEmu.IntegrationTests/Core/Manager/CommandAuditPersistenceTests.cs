using System.Net;
using System.Text.Json;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.Scripts;
using Moq;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class CommandAuditPersistenceTests : IAsyncLifetime
{
    private const uint ActorAccount = 4_100_434;
    private const uint ActorCharacter = 4_100_435;
    private const uint ActorObject = 4_100_436;
    private const uint TargetAccount = 4_100_437;
    private const uint TargetCharacter = 4_100_438;
    private const uint TargetObject = 4_100_439;
    private const string CommandName = "audit-test";

    public ValueTask InitializeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(AccountRole.NormalPlayer, false)]
    [InlineData(AccountRole.Moderator, true)]
    [InlineData(AccountRole.Admin, true)]
    public void Handle_OneDurableRowContainsActorSourceParsedArgumentsAndTargets(AccountRole role, bool allowed)
    {
        var command = new StaffCommand();
        var manager = Manager(role, command);

        Assert.True(manager.Handle(Actor(), "ALIAS \"two words\" \"quoted \\\"word\\\"\" café", Mock.Of<IMessageOutput>()));

        var row = ReadOnlyRow();
        Assert.Equal(allowed ? 1 : 0, command.Calls);
        Assert.Equal(CommandName, row.Command);
        Assert.Equal(ActorAccount, row.ActorAccountId);
        Assert.Equal(ActorCharacter, row.ActorCharacterId);
        Assert.Equal(role.ToString(), row.ActorRole);
        Assert.Equal("game-chat", row.Source);
        Assert.Equal("192.0.2.34", row.RemoteAddress);
        Assert.Equal(["two words", "quoted \"word\"", "café"], row.Arguments);
        Assert.Equal(allowed ? "completed" : "denied", row.Result);
        Assert.NotNull(row.CompletedAt);
        Assert.True(row.CompletedAt >= row.StartedAt);
        Assert.Contains(new CommandAuditTarget(ActorAccount, ActorCharacter, ActorObject, "actor"), row.Targets);
        Assert.Contains(new CommandAuditTarget(TargetAccount, TargetCharacter, TargetObject, "selected"), row.Targets);
        if (allowed)
            Assert.Single(row.Targets, target => target == new CommandAuditTarget(TargetAccount, TargetCharacter, TargetObject, "resolved"));
    }

    [Fact]
    public async Task Handle_DeferredCompletionUpdatesSameRowExactlyOnce()
    {
        var command = new DeferredCommand();
        var manager = Manager(AccountRole.Moderator, command);
        manager.Handle(Actor(), CommandName + " 30m \"a human reason\"", Mock.Of<IMessageOutput>());
        var started = ReadOnlyRow();
        Assert.Equal("started", started.Result);
        Assert.Null(started.CompletedAt);

        await Task.Run(() => command.Complete(false, "Login rejected the request."), TestContext.Current.CancellationToken);
        var completed = ReadOnlyRow();
        Assert.Equal(started.RequestId, completed.RequestId);
        Assert.Equal("rejected", completed.Result);
        Assert.Equal("Login rejected the request.", completed.Detail);
        Assert.NotNull(completed.CompletedAt);
        Assert.Contains(new CommandAuditTarget(TargetAccount, TargetCharacter, TargetObject, "resolved"), completed.Targets);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => command.Complete(true, "A duplicate must not replace the result."), TestContext.Current.CancellationToken)));
        var afterDuplicates = ReadOnlyRow();
        Assert.Equal(completed.RequestId, afterDuplicates.RequestId);
        Assert.Equal(completed.CompletedAt, afterDuplicates.CompletedAt);
        Assert.Equal(completed.Result, afterDuplicates.Result);
        Assert.Equal(completed.Detail, afterDuplicates.Detail);
    }

    [Fact]
    public void Handle_FailedAuditInsertPreventsHandlerAndAllowsLaterRetry()
    {
        Execute($"""
            CREATE TRIGGER `fail_command_audit_insert` BEFORE INSERT ON `command_audit`
            FOR EACH ROW BEGIN
                IF NEW.actor_account_id = {ActorAccount} THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected audit insert failure';
                END IF;
            END
            """);
        var command = new StaffCommand();
        var manager = Manager(AccountRole.Admin, command);
        var output = new Mock<IMessageOutput>();
        manager.Handle(Actor(), CommandName, output.Object);
        Assert.Equal(0, command.Calls);
        Assert.Equal(0L, CountRows());
        output.Verify(value => value.SendMessage("Command audit is unavailable. No command ran."), Times.Once);

        Execute("DROP TRIGGER `fail_command_audit_insert`");
        manager.Handle(Actor(), CommandName, output.Object);
        Assert.Equal(1, command.Calls);
        Assert.Equal("completed", ReadOnlyRow().Result);
    }

    [Fact]
    public void RejectWebApiCommand_RecordsTransportSourceWithoutClaimedActor()
    {
        var manager = new CommandManager(Mock.Of<IPermissionManager>(), new CommandAuditStore());
        manager.RejectWebApiCommand("192.0.2.45", CommandName, "{\"character\":\"Admin\"}");
        var row = ReadOnlyRow();
        Assert.Equal(0u, row.ActorAccountId);
        Assert.Equal(0u, row.ActorCharacterId);
        Assert.Equal("NormalPlayer", row.ActorRole);
        Assert.Equal("web-api", row.Source);
        Assert.Equal("192.0.2.45", row.RemoteAddress);
        Assert.Equal(["{\"character\":\"Admin\"}"], row.Arguments);
        Assert.Empty(row.Targets);
        Assert.Equal("denied", row.Result);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public void Store_CompletionCannotOverwriteAnEarlierResult()
    {
        var store = new CommandAuditStore();
        var entry = Entry();
        store.Start(entry);
        Assert.Throws<MySqlException>(() => store.Start(entry));
        entry.CompletedAt = DateTime.UtcNow;
        entry.Result = "completed";
        entry.Detail = "First result";
        store.Complete(entry);
        entry.Result = "failed";
        entry.Detail = "Duplicate result";

        Assert.Throws<InvalidOperationException>(() => store.Complete(entry));
        var row = ReadOnlyRow();
        Assert.Equal("completed", row.Result);
        Assert.Equal("First result", row.Detail);
    }

    [Fact]
    public void Store_PreservesFullUtf8CompletionDetails()
    {
        var store = new CommandAuditStore();
        var entry = Entry();
        store.Start(entry);
        entry.CompletedAt = DateTime.UtcNow;
        entry.Result = "completed";
        entry.Detail = "Reason with a 4-byte character 😀";
        store.Complete(entry);
        Assert.Equal(entry.Detail, ReadOnlyRow().Detail);
    }

    [Fact]
    public void Migration_CreatesTableAndSecondRunKeepsAuditRows()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SQL", "updates", "2026-09-11_aaemu_game_command_audit.sql"));
        sql = sql.Replace("`command_audit`", "`command_audit_migration_test`", StringComparison.Ordinal);
        Execute(sql);
        Execute("""
            INSERT INTO `command_audit_migration_test`
                (request_id, started_at, actor_account_id, actor_character_id, actor_role,
                 source, remote_address, command_name, arguments, targets, result)
            VALUES ('00000000-0000-0000-0000-000000000434', UTC_TIMESTAMP(6), 1, 2,
                'Moderator', 'game-chat', '192.0.2.1', 'audit-test', JSON_ARRAY('two words'),
                JSON_ARRAY(), 'started')
            """);

        Execute(sql);

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `command_audit_migration_test` WHERE request_id='00000000-0000-0000-0000-000000000434' AND result='started' AND completed_at IS NULL";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    private static CommandManager Manager(AccountRole role, ICommand command)
    {
        var permissions = new Mock<IPermissionManager>();
        permissions.Setup(value => value.GetRole(It.IsAny<ICharacter>())).Returns(role);
        var manager = new CommandManager(permissions.Object, new CommandAuditStore());
        manager.Register([CommandName, "alias"], command);
        return manager;
    }

    private static Character Actor()
    {
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Ip).Returns(IPAddress.Parse("192.0.2.34"));
        return new Character(new UnitCustomModelParams())
        {
            AccountId = ActorAccount,
            Id = ActorCharacter,
            ObjId = ActorObject,
            Connection = new GameConnection(session.Object),
            CurrentTarget = new Character(new UnitCustomModelParams())
                { AccountId = TargetAccount, Id = TargetCharacter, ObjId = TargetObject }
        };
    }

    private static CommandAuditEntry Entry() => new()
    {
        ActorAccountId = ActorAccount,
        ActorCharacterId = ActorCharacter,
        ActorRole = AccountRole.Admin,
        Command = CommandName,
        Arguments = ["a reason"]
    };

    private sealed record AuditRow(Guid RequestId, DateTime StartedAt, DateTime? CompletedAt,
        uint ActorAccountId, uint ActorCharacterId, string ActorRole, string Source,
        string RemoteAddress, string Command, string[] Arguments, CommandAuditTarget[] Targets,
        string Result, string Detail);

    private static AuditRow ReadOnlyRow()
    {
        Assert.Equal(1L, CountRows());
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_id, started_at, completed_at, actor_account_id, actor_character_id, actor_role, source, remote_address, command_name, arguments, targets, result, detail FROM command_audit WHERE command_name = @command";
        command.Parameters.AddWithValue("@command", CommandName);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new AuditRow(reader.GetGuid(0), reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.GetUInt32(3), reader.GetUInt32(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            JsonSerializer.Deserialize<string[]>(reader.GetString(9)),
            JsonSerializer.Deserialize<CommandAuditTarget[]>(reader.GetString(10)),
            reader.GetString(11), reader.GetString(12));
    }

    private static long CountRows()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM command_audit WHERE command_name = @command";
        command.Parameters.AddWithValue("@command", CommandName);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Cleanup()
    {
        Execute($"""
            DROP TRIGGER IF EXISTS `fail_command_audit_insert`;
            DROP TABLE IF EXISTS `command_audit_migration_test`;
            DELETE FROM `command_audit` WHERE command_name = '{CommandName}';
            """);
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [CommandPermission(GamePermission.StaffCommands)]
    private class StaffCommand : ICommand
    {
        public string[] CommandNames { get; set; } = [CommandName];
        public int Calls { get; private set; }
        public virtual void Execute(Character character, string[] args, IMessageOutput messageOutput)
        {
            Calls++;
            CommandAuditContext.RecordTarget(TargetAccount, TargetCharacter, TargetObject);
            CommandAuditContext.RecordTarget(TargetAccount, TargetCharacter, TargetObject);
        }
        public string GetCommandLineHelp() => "";
        public string GetCommandHelpText() => "";
    }

    [CommandPermission(GamePermission.StaffCommands)]
    private sealed class DeferredCommand : StaffCommand
    {
        public Action<bool, string> Complete { get; private set; }
        public override void Execute(Character character, string[] args, IMessageOutput messageOutput)
        {
            base.Execute(character, args, messageOutput);
            Complete = CommandAuditContext.DeferCompletion();
        }
    }
}
