using AAEmu.Login.Models;

namespace AAEmu.Login.Core.Launcher;

public interface ILaunchTicketStore
{
    Task<bool> StoreAsync(LauncherSessionPrincipal principal, byte[] ticketHash,
        DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<StoredLaunchTicket?> ConsumeAsync(byte[] ticketHash, CancellationToken cancellationToken);
}

public sealed record StoredLaunchTicket(
    ulong SessionId,
    AccountId AccountId,
    string Username,
    DateTimeOffset ExpiresAt);
