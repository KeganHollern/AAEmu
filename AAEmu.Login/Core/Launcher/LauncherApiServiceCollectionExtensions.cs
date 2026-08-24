using System.Threading.RateLimiting;
using AAEmu.Login.Models;

namespace AAEmu.Login.Core.Launcher;

public static class LauncherApiServiceCollectionExtensions
{
    public static IServiceCollection AddLauncherApi(this IServiceCollection services)
    {
        services.AddOptionsWithValidateOnStart<LauncherApiOptions>()
            .BindConfiguration(LauncherApiOptions.ConfigurationSectionName)
            .ValidateDataAnnotations()
            .Validate(options => !options.Enabled
                                 || (!string.IsNullOrWhiteSpace(options.ContentV2.ReleasePath)
                                     && IsPinnedSha256(options.ContentV2.ExpectedManifestSha256)
                                     && IsPinnedSha256(options.ContentV2.ExpectedMinisigSha256)),
                "Enabled launcher API requires a signed content release path and lowercase manifest/signature SHA-256 pins");
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoginReadiness, LoginReadiness>();
        services.AddSingleton<IClientContentBundleProvider, ClientContentBundleProvider>();
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

    private static bool IsPinnedSha256(string? value)
    {
        return value is { Length: 64 }
               && !value.All(character => character == '0')
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
