using AAEmu.Login.Models;
using AAEmu.Login.Utils;
using MySql.Data.MySqlClient;

namespace AAEmu.Login.Core.Launcher;

public sealed class MySqlLaunchTicketStore(
    IMySqlConnectionFactory connectionFactory,
    TimeProvider timeProvider) : ILaunchTicketStore
{
    public async Task<bool> StoreAsync(LauncherSessionPrincipal principal, byte[] ticketHash,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = connectionFactory.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = (MySqlTransaction)transaction;
            cleanup.CommandText = """
                DELETE FROM launcher_launch_tickets
                WHERE expires_at <= @now OR session_id = @sessionId
                """;
            cleanup.Parameters.AddWithValue("@now", now);
            cleanup.Parameters.AddWithValue("@sessionId", principal.SessionId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        var inserted = 0;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (MySqlTransaction)transaction;
            insert.CommandText = """
                INSERT INTO launcher_launch_tickets
                    (ticket_hash, session_id, username, expires_at, created_at)
                SELECT @ticketHash, sessions.id, @username, @expiresAt, @now
                FROM launcher_sessions AS sessions
                INNER JOIN users ON users.id = sessions.user_id
                WHERE sessions.id = @sessionId
                  AND sessions.user_id = @userId
                  AND sessions.revoked_at IS NULL
                  AND sessions.refresh_expires_at > @now
                  AND users.banned = 0
                """;
            insert.Parameters.AddWithValue("@ticketHash", ticketHash);
            insert.Parameters.AddWithValue("@sessionId", principal.SessionId);
            insert.Parameters.AddWithValue("@userId", principal.AccountId.Value);
            insert.Parameters.AddWithValue("@username", principal.Username);
            insert.Parameters.AddWithValue("@expiresAt", expiresAt.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("@now", now);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return inserted == 1;
    }

    public async Task<StoredLaunchTicket?> ConsumeAsync(byte[] ticketHash,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = connectionFactory.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        ulong ticketSessionId = default;
        AccountId accountId = default;
        string? username = null;
        DateTimeOffset expiresAt = default;
        var active = false;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (MySqlTransaction)transaction;
            select.CommandText = """
                SELECT tickets.session_id, sessions.user_id, tickets.username, tickets.expires_at,
                       sessions.revoked_at, sessions.refresh_expires_at, users.banned
                FROM launcher_launch_tickets AS tickets
                INNER JOIN launcher_sessions AS sessions ON sessions.id = tickets.session_id
                INNER JOIN users ON users.id = sessions.user_id
                WHERE tickets.ticket_hash = @ticketHash
                LIMIT 1
                FOR UPDATE
                """;
            select.Parameters.AddWithValue("@ticketHash", ticketHash);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                ticketSessionId = Convert.ToUInt64(reader["session_id"]);
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(reader["expires_at"]));
                active = reader.IsDBNull(reader.GetOrdinal("revoked_at"))
                         && Convert.ToInt64(reader["refresh_expires_at"]) > now
                         && !Convert.ToBoolean(reader["banned"])
                         && expiresAt.ToUnixTimeSeconds() > now;
                if (active)
                {
                    accountId = new AccountId(Convert.ToUInt32(reader["user_id"]));
                    username = Convert.ToString(reader["username"]);
                }
            }
        }

        if (ticketSessionId == default)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (MySqlTransaction)transaction;
            delete.CommandText = "DELETE FROM launcher_launch_tickets WHERE ticket_hash = @ticketHash";
            delete.Parameters.AddWithValue("@ticketHash", ticketHash);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return active
            ? new StoredLaunchTicket(ticketSessionId, accountId, username!, expiresAt)
            : null;
    }
}
