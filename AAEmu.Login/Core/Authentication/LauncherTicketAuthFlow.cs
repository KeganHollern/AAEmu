using AAEmu.Login.Core.Network.Connections;
using AAEmu.Login.Models;

namespace AAEmu.Login.Core.Authentication;

public sealed class LauncherTicketAuthFlow(AccountId accountId, string username) : IAuthenticationFlow
{
    public Task<AuthFlowResult> StartAsync(ILoginClient client, CancellationToken cancellationToken)
    {
        return Task.FromResult<AuthFlowResult>(new AuthFlowResult.Success(accountId, username));
    }
}
