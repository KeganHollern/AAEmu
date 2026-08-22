using AAEmu.Login.Models;
using AAEmu.Login.Utils;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AAEmu.Login.Core.Launcher;

public sealed class LauncherReadinessHealthCheck(
    ILoginReadiness readiness,
    IMySqlConnectionFactory connectionFactory,
    IClientCompactProvider compactProvider,
    IClientContentBundleProvider contentBundleProvider,
    IOptions<LauncherApiOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!readiness.IsInitialized)
            return HealthCheckResult.Unhealthy("Login initialization has not completed");
        if (options.Value.Enabled && !compactProvider.IsAvailable)
            return HealthCheckResult.Unhealthy("Launcher client compact is unavailable");
        if (options.Value.ContentV2.Enabled && !contentBundleProvider.IsAvailable)
            return HealthCheckResult.Unhealthy("Launcher v2 content bundle is unavailable");

        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = options.Value.Enabled
                ? """
                  SELECT COUNT(*)
                  FROM information_schema.columns
                  WHERE table_schema = DATABASE()
                    AND (
                      (table_name = 'launcher_sessions' AND column_name IN
                        ('id', 'user_id', 'access_token_hash', 'refresh_token_hash',
                         'access_expires_at', 'refresh_expires_at', 'created_at', 'updated_at', 'revoked_at'))
                      OR
                      (table_name = 'launcher_launch_tickets' AND column_name IN
                        ('ticket_hash', 'session_id', 'username', 'expires_at', 'created_at'))
                    )
                  """
                : "SELECT 1";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            var expected = options.Value.Enabled ? 14 : 1;
            return Convert.ToInt32(result) == expected
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Login database schema probe returned an unexpected result");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Login database probe failed", exception);
        }
    }
}
