using System.Security.Cryptography;
using System.Text;
using AAEmu.Login.Models;
using Microsoft.Extensions.Options;

namespace AAEmu.Login.Core.Launcher;

public sealed class LaunchTicketService(
    ILauncherSessionService sessionService,
    ILaunchTicketStore ticketStore,
    IOptions<LauncherApiOptions> options,
    TimeProvider timeProvider) : ILaunchTicketService
{
    private readonly bool _enabled = options.Value.Enabled;
    private readonly TimeSpan _ticketLifetime = TimeSpan.FromSeconds(options.Value.LaunchTicketLifetimeSeconds);

    public async Task<LauncherTicketResponse?> IssueAsync(LauncherSessionPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
            return null;
        if (!await sessionService.IsActiveAsync(principal.SessionId, principal.AccountId, cancellationToken))
            return null;

        var ticket = LauncherSessionService.CreateToken();
        var ticketHash = HashTicket(ticket);
        var expiresAt = timeProvider.GetUtcNow().Add(_ticketLifetime);
        if (!await ticketStore.StoreAsync(principal, ticketHash, expiresAt, cancellationToken))
            return null;

        return new LauncherTicketResponse(principal.Username, ticket, expiresAt);
    }

    public async Task<ConsumedLaunchTicket?> ConsumeAsync(string username, string ticket,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
            return null;
        if (!LauncherSessionService.IsWellFormedToken(ticket))
            return null;

        var ticketHash = HashTicket(ticket);
        var entry = await ticketStore.ConsumeAsync(ticketHash, cancellationToken);
        if (entry is null)
            return null;

        if (entry.ExpiresAt <= timeProvider.GetUtcNow()
            || !string.Equals(entry.Username, username, StringComparison.Ordinal)
            || !await sessionService.IsActiveAsync(entry.SessionId, entry.AccountId, cancellationToken))
        {
            return null;
        }

        return new ConsumedLaunchTicket(entry.AccountId, entry.Username);
    }

    private static byte[] HashTicket(string ticket) => SHA256.HashData(Encoding.ASCII.GetBytes(ticket));
}
