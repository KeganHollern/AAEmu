using System.Net;

using AAEmu.Game.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using NLog;

namespace AAEmu.Game.Services.Health;

public sealed class GameHealthService(IOptions<AppConfiguration> options, GameHealthState healthState) : IHostedService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private GameHealthServer _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        healthState.MarkNotReady();

        var config = options.Value.HealthNetwork;
        if (config is null)
        {
            Logger.Warn("Game health server configuration not found. Health checks will not start");
            return Task.CompletedTask;
        }

        var address = config.Host == "*" ? IPAddress.Any : IPAddress.Parse(config.Host);
        _server = new GameHealthServer(address, config.Port, healthState);
        if (!_server.Start())
        {
            throw new InvalidOperationException($"Failed to start Game health server on {config.Host}:{config.Port}");
        }

        Logger.Info($"Game health server started on {config.Host}:{config.Port}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server?.IsStarted ?? false)
        {
            _server.Stop();
        }

        Logger.Info("Game health server stopped");
        return Task.CompletedTask;
    }
}
