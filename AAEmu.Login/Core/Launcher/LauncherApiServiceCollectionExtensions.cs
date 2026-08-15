using System.Threading.RateLimiting;
using AAEmu.Login.Models;
using Microsoft.AspNetCore.RateLimiting;

namespace AAEmu.Login.Core.Launcher;

public static class LauncherApiServiceCollectionExtensions
{
    public static IServiceCollection AddLauncherApi(this IServiceCollection services)
    {
        services.AddOptionsWithValidateOnStart<LauncherApiOptions>()
            .BindConfiguration(LauncherApiOptions.ConfigurationSectionName)
            .ValidateDataAnnotations()
            .Validate(options => !options.Enabled
                                 || (!string.Equals(options.ExpectedClientCompactSha256, new string('0', 64),
                                         StringComparison.Ordinal)
                                     && options.ExpectedClientCompactSize > 1),
                "Enabled launcher API requires the expected client compact SHA-256 and size");
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoginReadiness, LoginReadiness>();
        services.AddSingleton<IClientCompactProvider, ClientCompactProvider>();
        services.AddSingleton<ILauncherSessionService, LauncherSessionService>();
        services.AddSingleton<ILaunchTicketStore, MySqlLaunchTicketStore>();
        services.AddSingleton<ILaunchTicketService, LaunchTicketService>();
        services.AddSingleton<LauncherReadinessHealthCheck>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("launcher-login", _ =>
                RateLimitPartition.GetFixedWindowLimiter(
                    "launcher-login-global",
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            options.AddPolicy("launcher-download", _ =>
                RateLimitPartition.GetConcurrencyLimiter(
                    "launcher-download-global",
                    static _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 4,
                        QueueLimit = 0
                    }));
        });
        return services;
    }
}
