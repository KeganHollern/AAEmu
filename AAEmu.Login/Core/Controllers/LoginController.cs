using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AAEmu.Login.Core.Authentication;
using AAEmu.Login.Core.Network.Connections;
using AAEmu.Login.Core.PacketHandlers.C2L;
using AAEmu.Login.Core.Packets.L2G;
using AAEmu.Login.Core.Services;
using AAEmu.Login.Models;
using AAEmu.Login.Utils;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;

namespace AAEmu.Login.Core.Controllers;

public partial class LoginController(
    IGameController gameController,
    IPasswordService passwordService,
    IOptions<AppConfiguration> appConfig,
    IOptions<KoreaAuthOptions> koreaOptions,
    IMySqlConnectionFactory connectionFactory,
    ILogger<LoginController> logger) : ILoginController
{
    private readonly bool _autoAccount = appConfig.Value.AutoAccount;
    private readonly KoreaAuthOptions _koreaOptions = koreaOptions.Value;

    private readonly ConcurrentDictionary<GameServerId, ConcurrentDictionary<uint, AccountId>>
        _tokens = []; // gsId, [token, accountId]

    // Allows Unicode letters and digits (any script), plus _ . - @. No control characters or newlines.
    [GeneratedRegex(@"^[\p{L}\p{Nd}_.\-@]{1,32}$")]
    private static partial Regex UsernameRegex();

    /// <summary>
    /// Eu Method Auth
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password sent by the client, with its encoding kind.</param>
    /// <param name="ip">The client IP address for recording.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<LoginResult> Login(string username, Password password, IPAddress ip,
        CancellationToken cancellationToken)
    {
        return await LoginInternalAsync(username, password, ip, allowPasswordRehash: true,
            createLegacyAccount: false, cancellationToken);
    }

    public async Task<LoginResult> LoginLauncherAsync(string username, string plaintextPassword, IPAddress ip,
        CancellationToken cancellationToken)
    {
        return await LoginInternalAsync(username, Password.FromPlaintext(plaintextPassword), ip,
            allowPasswordRehash: false, createLegacyAccount: true, cancellationToken);
    }

    private async Task<LoginResult> LoginInternalAsync(string username, Password password, IPAddress ip,
        bool allowPasswordRehash, bool createLegacyAccount, CancellationToken cancellationToken)
    {
        await using var connect = connectionFactory.CreateConnection();
        await using var command = connect.CreateCommand();
        command.CommandText = "SELECT * FROM users where username=@username";
        command.Parameters.AddWithValue("@username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            if (_autoAccount)
            {
                await reader.CloseAsync();
                return await CreateAndLoginInvalid(username, password, ip, connect, allowPasswordRehash,
                    createLegacyAccount, cancellationToken);
            }

            return new LoginResult(false, default, LoginDeniedReason.BadAccount);
        }

        var storedPassword = Convert.ToString(reader["password"])!;
        var storedKoreaChallengeHash = reader.IsDBNull(reader.GetOrdinal("korea_challenge_hash"))
            ? null
            : Convert.ToString(reader["korea_challenge_hash"]);

        var verificationResult = passwordService.VerifyPassword(storedPassword, password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return new LoginResult(false, default, LoginDeniedReason.BadAccount);
        }

        var banned = Convert.ToBoolean(reader["banned"]);
        if (banned)
        {
            var banReason = (LoginDeniedReason)(byte)Convert.ToUInt32(reader["ban_reason"]);
            return new LoginResult(false, default, banReason);
        }

        var accountId = new AccountId(Convert.ToUInt32(reader["id"]));
        var now = DateTime.UtcNow;

        await reader.CloseAsync();

        #region update account

        // Determine what needs rehashing, which is only possible when we have a plaintext password
        var rehashPbkdf2 = allowPasswordRehash
                           && verificationResult == PasswordVerificationResult.SuccessRehashNeeded
                           && password.Kind == PasswordKind.Plaintext;
        var koreaRehashNeeded = _koreaOptions.Enabled
                                && password.Kind == PasswordKind.Plaintext
                                && (storedKoreaChallengeHash == null
                                    || Sha256Crypt.ParseRounds(storedKoreaChallengeHash) != _koreaOptions.Rounds);

        command.Parameters.Clear();

        if (rehashPbkdf2 && koreaRehashNeeded)
        {
            command.CommandText =
                "UPDATE `users` SET password = @password, korea_challenge_hash = @koreaHash," +
                " last_ip = @last_ip, last_login = @last_login, updated_at = @updated_at WHERE id = @id";
            command.Parameters.AddWithValue("@password", passwordService.HashForStorage(password));
            command.Parameters.AddWithValue("@koreaHash",
                KoreaChallengeCrypt.Compute(password.Value, rounds: _koreaOptions.Rounds));
        }
        else if (rehashPbkdf2)
        {
            command.CommandText =
                "UPDATE `users` SET password = @password," +
                " last_ip = @last_ip, last_login = @last_login, updated_at = @updated_at WHERE id = @id";
            command.Parameters.AddWithValue("@password", passwordService.HashForStorage(password));
        }
        else if (koreaRehashNeeded)
        {
            command.CommandText =
                "UPDATE `users` SET korea_challenge_hash = @koreaHash," +
                " last_ip = @last_ip, last_login = @last_login, updated_at = @updated_at WHERE id = @id";
            command.Parameters.AddWithValue("@koreaHash",
                KoreaChallengeCrypt.Compute(password.Value, rounds: _koreaOptions.Rounds));
        }
        else
        {
            command.CommandText =
                "UPDATE `users` SET last_ip = @last_ip, last_login = @last_login, updated_at = @updated_at WHERE id = @id";
        }

        command.Parameters.AddWithValue("@id", accountId.Value);
        command.Parameters.AddWithValue("@last_ip", ip.ToString());
        command.Parameters.AddWithValue("@last_login", ((DateTimeOffset)now).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@updated_at", ((DateTimeOffset)now).ToUnixTimeSeconds());

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            logger.LogWarning("Database update failed, error occurred while updating account login IP and time");
        }

        #endregion

        return new LoginResult(true, accountId, default);
    }

    public async Task<KoreaAuthInfo?> GetKoreaAuthInfoAsync(string username, CancellationToken cancellationToken)
    {
        await using var connect = connectionFactory.CreateConnection();
        await using var command = connect.CreateCommand();
        command.CommandText =
            "SELECT id, korea_challenge_hash FROM users WHERE username = @username";
        command.Parameters.AddWithValue("@username", username);
        await using var reader = command.ExecuteReader();

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var accountId = new AccountId(reader.GetUInt32("id"));

        if (reader.IsDBNull(reader.GetOrdinal("korea_challenge_hash")))
            return null;

        var stored = reader.GetString("korea_challenge_hash");
        var rawHash = new byte[32];
        var (rounds, salt) = Sha256Crypt.Parse(stored, rawHash);
        return new KoreaAuthInfo(accountId, rawHash.AsMemory(), salt, rounds);
    }

    public async Task<(bool Banned, LoginDeniedReason BanReason)> CheckBanStatusAsync(
        AccountId accountId, CancellationToken cancellationToken)
    {
        await using var connect = connectionFactory.CreateConnection();
        await using var command = connect.CreateCommand();
        command.CommandText = "SELECT banned, ban_reason FROM users WHERE id = @accountId";
        command.Parameters.AddWithValue("@accountId", accountId.Value);
        await using var reader = command.ExecuteReader();

        if (!await reader.ReadAsync(cancellationToken))
            return (false, default);

        var banned = reader.GetBoolean("banned");
        var banReason = banned ? (LoginDeniedReason)(byte)reader.GetUInt32("ban_reason") : default;
        return (banned, banReason);
    }

    private async Task<LoginResult> CreateAndLoginInvalid(string username, Password password,
        IPAddress clientIp, MySqlConnection connection, bool allowPasswordRehash, bool createLegacyAccount,
        CancellationToken cancellationToken)
    {
        if (!UsernameRegex().IsMatch(username))
            return new LoginResult(false, default, LoginDeniedReason.BadAccount);

        var storagePassword = password;
        if (createLegacyAccount && password.Kind == PasswordKind.Plaintext)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password.Value));
            storagePassword = Password.FromSha256Hex(Convert.ToHexString(digest));
        }

        var passwordHash = passwordService.HashForStorage(storagePassword);

        await using var command = connection.CreateCommand();

        if (_koreaOptions.Enabled && password.Kind == PasswordKind.Plaintext)
        {
            var koreaHash = KoreaChallengeCrypt.Compute(password.Value, rounds: _koreaOptions.Rounds);
            command.CommandText =
                "INSERT into users (username, password, korea_challenge_hash, email, last_ip, last_login, created_at, updated_at)" +
                " VALUES (@username, @password, @koreaHash, @email, @last_ip, @last_login, @created_at, @updated_at)";
            command.Parameters.AddWithValue("@koreaHash", koreaHash);
        }
        else
        {
            command.CommandText =
                "INSERT into users (username, password, email, last_ip, last_login, created_at, updated_at)" +
                " VALUES (@username, @password, @email, @last_ip, @last_login, @created_at, @updated_at)";
        }

        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@password", passwordHash);
        command.Parameters.AddWithValue("@email", "");
        command.Parameters.AddWithValue("@last_ip", clientIp.ToString());
        command.Parameters.AddWithValue("@last_login", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@created_at", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@updated_at", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds());

        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return new LoginResult(false, default, LoginDeniedReason.LoginUnknown);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            // Another concurrent AutoAccount request created this username first.
            // Re-run normal authentication against the single canonical row.
            return await LoginInternalAsync(username, password, clientIp, allowPasswordRehash,
                createLegacyAccount, cancellationToken);
        }

        return await LoginInternalAsync(username, password, clientIp, allowPasswordRehash, createLegacyAccount,
            cancellationToken);
    }

    public void AddReconnectionToken(InternalConnection connection, GameServerId gsId, AccountId accountId, uint token)
    {
        var tokensForGameServer = _tokens.GetOrAdd(gsId, static _ => []);
        tokensForGameServer.TryAdd(token, accountId);
        connection.SendPacket(new LGPlayerReconnectPacket(token));
    }

    public Task<ReconnectResult> Reconnect(GameServerId gsId, AccountId accountId, uint token)
    {
        if (!_tokens.ContainsKey(gsId))
        {
            if (gameController.TryGetParentId(gsId, out var parentId))
                gsId = parentId;
            else
            {
                // TODO ...
                return Task.FromResult(new ReconnectResult(false, default));
            }
        }

        if (!_tokens[gsId].TryGetValue(token, out var value))
        {
            // TODO ...
            return Task.FromResult(new ReconnectResult(false, default));
        }

        if (value == accountId)
        {
            return Task.FromResult(new ReconnectResult(true, accountId));
        }

        // TODO ...
        return Task.FromResult(new ReconnectResult(false, default));
    }
}
