namespace AAEmu.Login.Core.Launcher;

public sealed class LauncherUpdateInitializer(
    ILauncherUpdateBundleProvider launcherUpdateBundleProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return launcherUpdateBundleProvider.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
