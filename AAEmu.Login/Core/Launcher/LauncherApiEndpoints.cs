using AAEmu.Login.Core.Controllers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
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

        var group = app.MapGroup("/launcher/v1");
        group.MapGet("/status", GetStatus);
        group.MapPost("/sessions", CreateSessionAsync).RequireRateLimiting("launcher-login");
        group.MapPost("/sessions/refresh", RefreshSessionAsync).RequireRateLimiting("launcher-login");
        group.MapDelete("/sessions/current", RevokeSessionAsync);
        group.MapGet("/me", GetAccountAsync);
        group.MapGet("/manifest", GetManifestAsync);
        group.MapGet("/assets/client.sqlite3", DownloadClientCompactAsync)
            .RequireRateLimiting("launcher-download");
        group.MapPost("/launch-tickets", CreateLaunchTicketAsync).RequireRateLimiting("launcher-login");
    }

    private static IResult GetStatus(
        ILoginReadiness readiness,
        IGameController gameController,
        IClientCompactProvider compactProvider)
    {
        if (!readiness.IsInitialized || !compactProvider.IsAvailable)
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

    private static async Task<IResult> GetAccountAsync(
        HttpContext context,
        ILauncherSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        return principal is null
            ? Results.Unauthorized()
            : Results.Ok(new LauncherAccountResponse(principal.AccountId.Value, principal.Username));
    }

    private static async Task<IResult> GetManifestAsync(
        HttpContext context,
        ILoginReadiness readiness,
        ILauncherSessionService sessionService,
        IClientCompactProvider compactProvider,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        return readiness.IsInitialized && compactProvider.IsAvailable
            ? Results.Ok(compactProvider.Manifest)
            : Maintenance();
    }

    private static async Task<IResult> DownloadClientCompactAsync(
        HttpContext context,
        ILoginReadiness readiness,
        ILauncherSessionService sessionService,
        IClientCompactProvider compactProvider,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, sessionService, cancellationToken);
        if (principal is null)
            return Results.Unauthorized();
        if (!readiness.IsInitialized || !compactProvider.IsAvailable)
            return Maintenance();

        var manifest = compactProvider.Manifest;
        return Results.File(
            compactProvider.FilePath,
            "application/vnd.sqlite3",
            "client.sqlite3",
            entityTag: new EntityTagHeaderValue($"\"sha256-{manifest.Sha256}\""),
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

    private static IResult InvalidCredentials() => Results.Json(
        new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Maintenance() => Results.Json(
        new { error = "maintenance" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
