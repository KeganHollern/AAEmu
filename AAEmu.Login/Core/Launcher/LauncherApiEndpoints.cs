using System.Net;
using AAEmu.Login.Core.Controllers;
using AAEmu.Login.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using MySql.Data.MySqlClient;

namespace AAEmu.Login.Core.Launcher;

public static class LauncherApiEndpoints
{
    private const int MaxUsernameLength = 32;
    private const int MaxPasswordLength = 256;
    private const int MaxRequestBodyBytes = 8 * 1024;

    public static void MapLauncherApi(this WebApplication app)
    {
        app.UseRateLimiter();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LauncherApi");
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/launcher"))
            {
                await next(context);
                return;
            }

            var maxBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBodySize is { IsReadOnly: false })
                maxBodySize.MaxRequestBodySize = MaxRequestBodyBytes;
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";

            if (context.Request.ContentLength > MaxRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            try
            {
                await next(context);
            }
            catch (Exception exception) when (exception is MySqlException or IOException)
            {
                logger.LogWarning(exception, "Launcher API dependency is unavailable");
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "maintenance" });
                }
            }
        });

        var launcherOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<LauncherApiOptions>>().Value;
        if (launcherOptions.Enabled)
        {
            var group = app.MapGroup("/launcher/v1");
            group.MapGet("/status", GetStatus);
            group.MapPost("/sessions", CreateSessionAsync).RequireRateLimiting("launcher-login");
            group.MapPost("/sessions/refresh", RefreshSessionAsync).RequireRateLimiting("launcher-login");
            group.MapDelete("/sessions/current", RevokeSessionAsync);
            group.MapPost("/launch-tickets", CreateLaunchTicketAsync).RequireRateLimiting("launcher-login");

            var content = app.MapGroup("/launcher/v2");
            content.MapGet("/manifest", GetContentManifestAsync);
            content.MapGet("/manifest.minisig", GetContentMinisigAsync);
            content.MapGet("/assets/{sha256}", DownloadContentAssetAsync)
                .RequireRateLimiting("launcher-download");

            // In-game web test page (aaemu-cluster#26): the ArcheAge client's embedded
            // Awesomium browser (Chromium ~18) opens the TrionWeb base URLs and appends
            // its own path suffixes. Accept any suffix and echo the request back in
            // deliberately ancient, self-contained HTML so we can prove the URLs are
            // server-controlled and learn the exact path each client surface requests.
            app.MapGet("/launcher/test-shop-ui", GetTestShopUi);
            app.MapGet("/launcher/test-shop-ui/{**clientPath}", GetTestShopUi);
        }

        var launcherUpdateOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<LauncherUpdateOptions>>().Value;
        if (launcherUpdateOptions.Enabled)
        {
            var update = app.MapGroup("/launcher/update/v1");
            update.MapGet("/manifest", GetLauncherUpdateManifest);
            update.MapGet("/manifest.minisig", GetLauncherUpdateMinisig);
            update.MapGet(
                    $"/{LauncherUpdateBundleProvider.LinuxArchiveFileName}",
                    GetLauncherUpdateLinuxArchive)
                .RequireRateLimiting("launcher-download");
            update.MapGet(
                    $"/{LauncherUpdateBundleProvider.WindowsArchiveFileName}",
                    GetLauncherUpdateWindowsArchive)
                .RequireRateLimiting("launcher-download");
        }
    }

    private static IResult GetLauncherUpdateManifest(ILauncherUpdateBundleProvider updateProvider)
    {
        return !updateProvider.IsAvailable
            ? Maintenance()
            : GetLauncherUpdateAsset(updateProvider.Manifest, "application/json");
    }

    private static IResult GetLauncherUpdateMinisig(ILauncherUpdateBundleProvider updateProvider)
    {
        return !updateProvider.IsAvailable
            ? Maintenance()
            : GetLauncherUpdateAsset(updateProvider.Minisig, "application/octet-stream");
    }

    private static IResult GetLauncherUpdateLinuxArchive(ILauncherUpdateBundleProvider updateProvider)
    {
        return !updateProvider.IsAvailable
            ? Maintenance()
            : GetLauncherUpdateAsset(updateProvider.LinuxArchive, "application/gzip");
    }

    private static IResult GetLauncherUpdateWindowsArchive(ILauncherUpdateBundleProvider updateProvider)
    {
        return !updateProvider.IsAvailable
            ? Maintenance()
            : GetLauncherUpdateAsset(updateProvider.WindowsArchive, "application/zip");
    }

    private static IResult GetLauncherUpdateAsset(
        LauncherUpdateAsset asset,
        string contentType)
    {
        return Results.Stream(
            asset.OpenReadStream(),
            contentType,
            entityTag: ContentEntityTag(asset.Sha256),
            enableRangeProcessing: true);
    }

    private static IResult GetTestShopUi(HttpContext context)
    {
        var requestedPath = WebUtility.HtmlEncode(context.Request.Path.Value ?? "/");
        var query = WebUtility.HtmlEncode(context.Request.QueryString.Value ?? "");
        var userAgent = WebUtility.HtmlEncode(context.Request.Headers.UserAgent.ToString());
        var utcNow = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        var html = $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <title>Hyprlane Shop Test</title>
            <style>
            body { font-family: Georgia, serif; background: #f6efe2; color: #3b2f1e; margin: 24px; }
            .card { background: #fffdf7; border: 2px solid #c8b184; padding: 16px 20px; }
            h1 { color: #7a5b16; font-size: 28px; margin: 0 0 8px 0; }
            code { background: #efe6d2; padding: 2px 5px; }
            .ok { color: #1c7a2d; font-weight: bold; }
            .bad { color: #a33333; font-weight: bold; }
            </style>
            </head>
            <body>
            <div class="card">
            <h1>Hyprlane in-game browser test</h1>
            <p class="ok">HTML + CSS render OK.</p>
            <p>Requested path: <code>{{requestedPath}}</code></p>
            <p>Query: <code>{{query}}</code></p>
            <p>User agent: <code>{{userAgent}}</code></p>
            <p>Server time (UTC): <code>{{utcNow}}</code></p>
            <p id="js">JavaScript: <span class="bad">NOT running</span></p>
            <script type="text/javascript">
            document.getElementById('js').innerHTML = 'JavaScript: <span class="ok">running (ES5)</span>';
            </script>
            <p><a href="/launcher/test-shop-ui/clicked-a-link">Follow a link</a></p>
            </div>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static IResult GetStatus(
        ILoginReadiness readiness,
        IGameController gameController,
        IClientContentBundleProvider contentProvider)
    {
        if (!readiness.IsInitialized || !contentProvider.IsAvailable)
        {
            return Results.Json(
                new LauncherStatusResponse(false, false, "Launcher services are under maintenance"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new LauncherStatusResponse(true, gameController.HasActiveGameServer));
    }

    private static async Task<IResult> CreateSessionAsync(
        LauncherLoginRequest request,
        HttpContext context,
        ILoginReadiness readiness,
        ILoginController loginController,
        ILauncherSessionService sessionService,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsInitialized)
            return Maintenance();
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > MaxUsernameLength
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPasswordLength)
        {
            return InvalidCredentials();
        }

        var login = await loginController.LoginLauncherAsync(
            request.Username, request.Password,
            context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None, cancellationToken);
        if (!login.Success)
            return InvalidCredentials();

        var session = await sessionService.CreateAsync(login.AccountId, request.Username, cancellationToken);
        return Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RefreshSessionAsync(
        LauncherRefreshRequest request,
        ILoginReadiness readiness,
        ILauncherSessionService sessionService,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsInitialized)
            return Maintenance();
        var session = await sessionService.RefreshAsync(request.RefreshToken ?? string.Empty, cancellationToken);
        return session is null ? Results.Unauthorized() : Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RevokeSessionAsync(
        HttpContext context,
        ILauncherSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        await sessionService.RevokeAsync(principal.SessionId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetContentManifestAsync(
        HttpContext context,
        ILoginReadiness readiness,
        ILauncherSessionService sessionService,
        IClientContentBundleProvider contentProvider,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        if (!readiness.IsInitialized || !contentProvider.IsAvailable)
            return Maintenance();

        return Results.File(
            contentProvider.ManifestBytes.ToArray(),
            "application/json",
            entityTag: ContentEntityTag(contentProvider.ManifestSha256));
    }

    private static async Task<IResult> GetContentMinisigAsync(
        HttpContext context,
        ILoginReadiness readiness,
        ILauncherSessionService sessionService,
        IClientContentBundleProvider contentProvider,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        if (!readiness.IsInitialized || !contentProvider.IsAvailable)
            return Maintenance();

        return Results.File(
            contentProvider.MinisigBytes.ToArray(),
            "application/octet-stream",
            entityTag: ContentEntityTag(contentProvider.MinisigSha256));
    }

    private static async Task<IResult> DownloadContentAssetAsync(
        string sha256,
        HttpContext context,
        ILoginReadiness readiness,
        ILauncherSessionService sessionService,
        IClientContentBundleProvider contentProvider,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        if (!readiness.IsInitialized || !contentProvider.IsAvailable)
            return Maintenance();
        if (!IsLowerSha256(sha256) || !contentProvider.TryGetAsset(sha256, out var asset))
            return Results.NotFound();

        return Results.Stream(
            asset.OpenReadStream(),
            "application/octet-stream",
            entityTag: ContentEntityTag(asset.Sha256),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> CreateLaunchTicketAsync(
        HttpContext context,
        ILoginReadiness readiness,
        IGameController gameController,
        ILauncherSessionService sessionService,
        ILaunchTicketService launchTicketService,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        if (!readiness.IsInitialized || !gameController.HasActiveGameServer)
            return Maintenance();

        var ticket = await launchTicketService.IssueAsync(principal, cancellationToken);
        return ticket is null ? Results.Unauthorized() : Results.Ok(ticket);
    }

    private static async Task<LauncherSessionPrincipal?> AuthenticateAsync(
        HttpContext context,
        ILauncherSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = authorization[prefix.Length..];
        return await sessionService.AuthenticateAccessTokenAsync(token, cancellationToken);
    }

    private static LauncherTokenResponse ToResponse(LauncherSessionTokens session) => new(
        session.AccessToken,
        session.AccessTokenExpiresAt,
        session.RefreshToken,
        session.RefreshTokenExpiresAt,
        session.Principal.Username);

    private static EntityTagHeaderValue ContentEntityTag(string sha256) =>
        new($"\"sha256-{sha256}\"");

    private static bool IsLowerSha256(string? value)
    {
        return value is { Length: 64 }
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static IResult InvalidCredentials() => Results.Json(
        new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Maintenance() => Results.Json(
        new { error = "maintenance" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
