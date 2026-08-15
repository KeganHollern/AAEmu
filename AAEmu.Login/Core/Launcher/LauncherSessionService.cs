using System.Security.Cryptography;
using System.Text;
using AAEmu.Login.Models;
using AAEmu.Login.Utils;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;

namespace AAEmu.Login.Core.Launcher;

public sealed class LauncherSessionService(
    IMySqlConnectionFactory connectionFactory,
    IOptions<LauncherApiOptions> options,
    TimeProvider timeProvider) : ILauncherSessionService
{
    private const int TokenBytes = 32;
    private const int TokenCharacters = TokenBytes * 2;
    private readonly LauncherApiOptions _options = options.Value;

    public async Task<LauncherSessionTokens> CreateAsync(AccountId accountId, string username,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var accessExpiresAt = now.AddMinutes(_options.AccessTokenLifetimeMinutes);
        var refreshExpiresAt = now.AddDays(_options.RefreshTokenLifetimeDays);
        var accessToken = CreateToken();
        var refreshToken = CreateToken();

        await using var connection = connectionFactory.CreateConnection();
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText =
                "DELETE FROM launcher_sessions WHERE refresh_expires_at <= @now OR revoked_at IS NOT NULL";
            cleanup.Parameters.AddWithValue("@now", now.ToUnixTimeSeconds());
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO launcher_sessions
                (user_id, access_token_hash, refresh_token_hash, access_expires_at,
                 refresh_expires_at, created_at, updated_at)
            VALUES
                (@userId, @accessHash, @refreshHash, @accessExpiresAt,
                 @refreshExpiresAt, @createdAt, @updatedAt)
            """;
        command.Parameters.AddWithValue("@userId", accountId.Value);
        command.Parameters.AddWithValue("@accessHash", HashToken(accessToken));
        command.Parameters.AddWithValue("@refreshHash", HashToken(refreshToken));
        command.Parameters.AddWithValue("@accessExpiresAt", accessExpiresAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@refreshExpiresAt", refreshExpiresAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@createdAt", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@updatedAt", now.ToUnixTimeSeconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Failed to create launcher session");
        var sessionId = Convert.ToUInt64(command.LastInsertedId);

        return new LauncherSessionTokens(
            new LauncherSessionPrincipal(sessionId, accountId, username),
            accessToken,
            accessExpiresAt,
            refreshToken,
            refreshExpiresAt);
    }

    public async Task<LauncherSessionPrincipal?> AuthenticateAccessTokenAsync(string token,
        CancellationToken cancellationToken)
    {
        if (!IsWellFormedToken(token))
            return null;

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = connectionFactory.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sessions.id, sessions.user_id, users.username
            FROM launcher_sessions AS sessions
            INNER JOIN users ON users.id = sessions.user_id
            WHERE sessions.access_token_hash = @tokenHash
              AND sessions.revoked_at IS NULL
              AND sessions.access_expires_at > @now
              AND sessions.refresh_expires_at > @now
              AND users.banned = 0
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@tokenHash", HashToken(token));
        command.Parameters.AddWithValue("@now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new LauncherSessionPrincipal(
            Convert.ToUInt64(reader["id"]),
            new AccountId(Convert.ToUInt32(reader["user_id"])),
            Convert.ToString(reader["username"])!);
    }

    public async Task<LauncherSessionTokens?> RefreshAsync(string refreshToken,
        CancellationToken cancellationToken)
    {
        if (!IsWellFormedToken(refreshToken))
            return null;

        var now = timeProvider.GetUtcNow();
        await using var connection = connectionFactory.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        ulong sessionId = default;
        AccountId accountId = default;
        string? username = null;
        var canRefresh = false;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (MySqlTransaction)transaction;
            select.CommandText = """
                SELECT sessions.id, sessions.user_id, users.username, users.banned
                FROM launcher_sessions AS sessions
                INNER JOIN users ON users.id = sessions.user_id
                WHERE sessions.refresh_token_hash = @tokenHash
                  AND sessions.revoked_at IS NULL
                  AND sessions.refresh_expires_at > @now
                LIMIT 1
                FOR UPDATE
                """;
            select.Parameters.AddWithValue("@tokenHash", HashToken(refreshToken));
            select.Parameters.AddWithValue("@now", now.ToUnixTimeSeconds());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && !Convert.ToBoolean(reader["banned"]))
            {
                sessionId = Convert.ToUInt64(reader["id"]);
                accountId = new AccountId(Convert.ToUInt32(reader["user_id"]));
                username = Convert.ToString(reader["username"])!;
                canRefresh = true;
            }
        }

        if (!canRefresh)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var newAccessToken = CreateToken();
        var newRefreshToken = CreateToken();
        var accessExpiresAt = now.AddMinutes(_options.AccessTokenLifetimeMinutes);
        var refreshExpiresAt = now.AddDays(_options.RefreshTokenLifetimeDays);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (MySqlTransaction)transaction;
            update.CommandText = """
                UPDATE launcher_sessions
                SET access_token_hash = @accessHash,
                    refresh_token_hash = @refreshHash,
                    access_expires_at = @accessExpiresAt,
                    refresh_expires_at = @refreshExpiresAt,
                    updated_at = @updatedAt
                WHERE id = @sessionId AND revoked_at IS NULL
                """;
            update.Parameters.AddWithValue("@accessHash", HashToken(newAccessToken));
            update.Parameters.AddWithValue("@refreshHash", HashToken(newRefreshToken));
            update.Parameters.AddWithValue("@accessExpiresAt", accessExpiresAt.ToUnixTimeSeconds());
            update.Parameters.AddWithValue("@refreshExpiresAt", refreshExpiresAt.ToUnixTimeSeconds());
            update.Parameters.AddWithValue("@updatedAt", now.ToUnixTimeSeconds());
            update.Parameters.AddWithValue("@sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new LauncherSessionTokens(
            new LauncherSessionPrincipal(sessionId, accountId, username!),
            newAccessToken,
            accessExpiresAt,
            newRefreshToken,
            refreshExpiresAt);
    }

    public async Task RevokeAsync(ulong sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = connectionFactory.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE launcher_sessions
            SET revoked_at = COALESCE(revoked_at, @now), updated_at = @now
            WHERE id = @sessionId
            """;
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@sessionId", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsActiveAsync(ulong sessionId, AccountId accountId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = connectionFactory.CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM launcher_sessions AS sessions
            INNER JOIN users ON users.id = sessions.user_id
            WHERE sessions.id = @sessionId
              AND sessions.user_id = @userId
              AND sessions.revoked_at IS NULL
              AND sessions.refresh_expires_at > @now
              AND users.banned = 0
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@userId", accountId.Value);
        command.Parameters.AddWithValue("@now", now);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    internal static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));

    internal static byte[] HashToken(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    internal static bool IsWellFormedToken(string token) => token.Length == TokenCharacters
        && token.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
