using System.Text.Json;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Utils.Scripts;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers;

public sealed class CommandAuditStore : ICommandAuditStore
{
    private readonly Func<MySqlConnection> _openConnection;

    public CommandAuditStore() : this(MySQL.CreateConnection)
    {
    }

    internal CommandAuditStore(Func<MySqlConnection> openConnection)
    {
        _openConnection = openConnection;
    }

    public void Start(CommandAuditEntry entry)
    {
        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO command_audit
                (request_id, started_at, actor_account_id, actor_character_id, actor_role,
                 source, remote_address, command_name, arguments, targets, result)
            VALUES (@request, @started, @account, @character, @role, @source, @address,
                    @command, @arguments, @targets, 'started')
            """;
        command.Parameters.AddWithValue("@request", entry.RequestId.ToString("D"));
        command.Parameters.AddWithValue("@started", entry.StartedAt);
        command.Parameters.AddWithValue("@account", entry.ActorAccountId);
        command.Parameters.AddWithValue("@character", entry.ActorCharacterId);
        command.Parameters.AddWithValue("@role", entry.ActorRole.ToString());
        command.Parameters.AddWithValue("@source", entry.Source);
        command.Parameters.AddWithValue("@address", entry.RemoteAddress);
        command.Parameters.AddWithValue("@command", entry.Command);
        command.Parameters.AddWithValue("@arguments", JsonSerializer.Serialize(entry.Arguments));
        command.Parameters.AddWithValue("@targets", JsonSerializer.Serialize(entry.Targets));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The command audit could not start.");
    }

    public void Complete(CommandAuditEntry entry)
    {
        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE command_audit
               SET completed_at = @completed, targets = @targets, result = @result, detail = @detail
             WHERE request_id = @request AND completed_at IS NULL
            """;
        command.Parameters.AddWithValue("@request", entry.RequestId.ToString("D"));
        command.Parameters.AddWithValue("@completed", entry.CompletedAt);
        command.Parameters.AddWithValue("@targets", JsonSerializer.Serialize(entry.Targets));
        command.Parameters.AddWithValue("@result", entry.Result);
        command.Parameters.AddWithValue("@detail", entry.Detail);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The command audit could not finish.");
    }
}
