using AAEmu.Login.Models;

namespace AAEmu.Login.Core.Launcher;

public sealed record LauncherLoginRequest(string Username, string Password);

public sealed record LauncherRefreshRequest(string RefreshToken);

public sealed record LauncherTokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string Username);

public sealed record LauncherStatusResponse(bool Available, bool GameAvailable, string? Message = null);

public sealed record LauncherTicketResponse(string Username, string Ticket, DateTimeOffset ExpiresAt);

public sealed record LauncherSessionPrincipal(ulong SessionId, AccountId AccountId, string Username);

public sealed record LauncherSessionTokens(
    LauncherSessionPrincipal Principal,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record ConsumedLaunchTicket(AccountId AccountId, string Username);
