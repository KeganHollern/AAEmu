using AAEmu.Login.Models;

namespace AAEmu.Login.Core.Launcher;

public interface ILauncherSessionService
{
    Task<LauncherSessionTokens> CreateAsync(AccountId accountId, string username,
        CancellationToken cancellationToken);
    Task<LauncherSessionPrincipal?> AuthenticateAccessTokenAsync(string token,
        CancellationToken cancellationToken);
    Task<LauncherSessionTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(ulong sessionId, CancellationToken cancellationToken);
    Task<bool> IsActiveAsync(ulong sessionId, AccountId accountId, CancellationToken cancellationToken);
}
