namespace AAEmu.Login.Core.Launcher;

public interface ILaunchTicketService
{
    Task<LauncherTicketResponse?> IssueAsync(LauncherSessionPrincipal principal,
        CancellationToken cancellationToken);
    Task<ConsumedLaunchTicket?> ConsumeAsync(string username, string ticket,
        CancellationToken cancellationToken);
}
