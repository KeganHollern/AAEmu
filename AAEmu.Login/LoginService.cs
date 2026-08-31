using AAEmu.Commons.Utils.Updater;
using AAEmu.Login.Core.Controllers;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Core.Network.Internal;
using AAEmu.Login.Models;
using AAEmu.Login.Utils;
using Microsoft.Extensions.Options;

namespace AAEmu.Login;

public sealed class LoginService(
    IGameController gameController,
    IRequestController requestController,
    IInternalNetwork internalNetwork,
    ILoginReadiness loginReadiness,
    IClientContentBundleProvider clientContentBundleProvider,
    IMySqlConnectionFactory connectionFactory,
    IOptions<DBConnectionsConfig> dbConnectionsConfig,
    ILogger<LoginService> logger) : IHostedService, IDisposable
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        loginReadiness.MarkUnavailable();
        logger.LogInformation("Starting daemon: AAEmu.Login");

        // Check for updates
        await using (var connection = connectionFactory.CreateConnection())
        {
            if (!MySqlDatabaseUpdater.Run(connection, "aaemu_login",
                    dbConnectionsConfig.Value.MySQLProvider.Database,
                    dbConnectionsConfig.Value.AutoApplyUpdates))
            {
                logger.LogCritical("Failed to update database!");
                throw new InvalidOperationException("The login database update failed.");
            }
        }

        requestController.Initialize();
        gameController.Load();
        await clientContentBundleProvider.InitializeAsync(cancellationToken);
        internalNetwork.Start();
        loginReadiness.MarkInitialized();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        loginReadiness.MarkUnavailable();
        logger.LogInformation("Stopping daemon.");
        internalNetwork.Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        logger.LogInformation("Disposing...");
    }
}
