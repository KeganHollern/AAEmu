using AAEmu.Commons.Network;
using AAEmu.Login.Core.Authentication;
using AAEmu.Login.Core.Controllers;
using AAEmu.Login.Core.Network.Login;
using AAEmu.Login.Core.PacketHandlers.C2L;
using AAEmu.Login.Models;
using Microsoft.Extensions.Options;

namespace AAEmu.Login.Core.Network.Connections;

/// <summary>
/// Manages the login flow state machine for a client connection.
/// The instance members of this class are thread-safe.
/// </summary>
public sealed class LoginSession(
    ILoginConnection connection,
    IGameController gameController,
    TimeProvider timeProvider,
    IOptions<AppConfiguration> appConfig,
    ILogger<LoginSession> logger)
    : ILoginSession
{
    private const string AuthenticationCompletedEventName = "login.authentication.completed";
    private const string EnterWorldCompletedEventName = "login.enter_world.completed";
    private readonly TimeSpan _enterWorldTimeout = appConfig.Value.EnterWorldTimeout;
    private readonly Lock _lock = new();
    private readonly ConnectionEventLimiter _authenticationDenialEvents = new();

    private LoginState _state = LoginState.Connected;
    private IAuthenticationFlow? _currentAuthFlow;
    private CancellationTokenSource? _enterWorldCts;
    private TaskCompletionSource<EnterWorldResult>? _enterWorldTcs;
    private GameServerId? _pendingGsId;
    private Task? _enterWorldBackgroundTask;

    internal Task? EnterWorldBackgroundTask => _enterWorldBackgroundTask;

    public ILoginConnection Connection { get; } = connection;

    public ILoginClient Client { get; } = new LoginClient(connection);

    public LoginState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public IAuthenticationFlow? CurrentAuthFlow
    {
        get
        {
            lock (_lock)
            {
                return _currentAuthFlow;
            }
        }
    }

    public ValueTask SendPacketAsync(LoginPacket packet, CancellationToken cancellationToken) =>
        Connection.SendPacketAsync(packet, cancellationToken);

    public async Task AuthenticateAsync(IAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        bool canProceed;

        lock (_lock)
        {
            if (_state != LoginState.Connected)
            {
                if (_authenticationDenialEvents.TryConsume())
                {
                    logger.LogWarning(
                        "Cannot authenticate: invalid state {State} for connection {ConnectionId}",
                        _state, Connection.Id);
                }
                canProceed = false;
            }
            else
            {
                _state = LoginState.Authenticating;
                canProceed = true;
            }
        }

        if (!canProceed)
        {
            await SendAuthResultAsync(new AuthFlowResult.Denied(LoginDeniedReason.BadResponse), cancellationToken);
            return;
        }

        var result = await flow.StartAsync(Client, cancellationToken);
        ProcessAuthResult(result, flow);
        LogAuthenticationOutcome(result, flow);
        await SendAuthResultAsync(result, cancellationToken);
    }

    public async Task ContinueAuthAsync<TFlow>(Func<TFlow, Task<AuthFlowResult>> step,
        CancellationToken cancellationToken)
        where TFlow : class, IAuthenticationFlow
    {
        TFlow? flow;
        LoginDeniedReason? earlyDenial = null;

        lock (_lock)
        {
            if (_state != LoginState.Authenticating)
            {
                if (_authenticationDenialEvents.TryConsume())
                {
                    logger.LogWarning(
                        "Cannot continue auth: invalid state {State} for connection {ConnectionId}",
                        _state, Connection.Id);
                }
                flow = null;
                earlyDenial = LoginDeniedReason.BadResponse;
            }
            else if (_currentAuthFlow is not TFlow typedFlow)
            {
                if (_authenticationDenialEvents.TryConsume())
                {
                    logger.LogWarning(
                        "Auth flow type mismatch: expected {Expected}, got {Actual} for connection {ConnectionId}",
                        typeof(TFlow).Name, _currentAuthFlow?.GetType().Name ?? "null", Connection.Id);
                }
                _currentAuthFlow = null;
                _state = LoginState.Disconnected;
                flow = null;
                earlyDenial = LoginDeniedReason.BadResponse;
                Connection.Shutdown();
            }
            else
            {
                flow = typedFlow;
            }
        }

        if (earlyDenial is not null)
        {
            await SendAuthResultAsync(new AuthFlowResult.Denied(earlyDenial.Value), cancellationToken);
            return;
        }

        var result = await step(flow!);
        ProcessAuthResult(result, flow!);
        LogAuthenticationOutcome(result, flow!);
        await SendAuthResultAsync(result, cancellationToken);
    }

    private async Task SendAuthResultAsync(AuthFlowResult result, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case AuthFlowResult.Success success:
                await Client.SendAuthSuccessAsync(success.AccountId, cancellationToken);
                break;

            case AuthFlowResult.Denied denied:
                await Client.SendAuthDeniedAsync(denied.Reason, cancellationToken);
                break;

            // Pending: flow is managing itself, nothing to send
        }
    }

    private void ProcessAuthResult(AuthFlowResult result, IAuthenticationFlow flow)
    {
        lock (_lock)
        {
            switch (result)
            {
                case AuthFlowResult.Success success:
                    Connection.AccountId = success.AccountId;
                    if (success.Username is not null)
                        Connection.AccountName = success.Username;
                    Connection.LastLogin = timeProvider.GetUtcNow().DateTime;
                    Connection.LastIp = Connection.Ip;
                    _currentAuthFlow = null;
                    _state = LoginState.Authenticated;
                    break;

                case AuthFlowResult.Denied:
                    _currentAuthFlow = null;
                    _state = LoginState.Connected;
                    break;

                case AuthFlowResult.Pending:
                    _currentAuthFlow = flow;
                    // Stay in Authenticating
                    break;
            }
        }
    }

    private void LogAuthenticationOutcome(AuthFlowResult result, IAuthenticationFlow flow)
    {
        if (result is AuthFlowResult.Pending)
            return;

        if (result is AuthFlowResult.Denied && !_authenticationDenialEvents.TryConsume())
            return;

        var outcome = result is AuthFlowResult.Success ? "success" : "denied";
        var reason = result is AuthFlowResult.Denied denied ? denied.Reason.ToString() : null;

        logger.LogInformation(
            "{EventName}: Authentication {Outcome} on connection {ConnectionId} with {AuthenticationMethod}. " +
            "Reason {Reason}",
            AuthenticationCompletedEventName, outcome, Connection.Id.Value, GetAuthenticationMethod(flow), reason);
    }

    private static string GetAuthenticationMethod(IAuthenticationFlow flow) => flow switch
    {
        LauncherTicketAuthFlow => "launcher_ticket",
        PasswordAuthFlow => "password",
        ReconnectAuthFlow => "reconnect",
        KoreaAuthFlow => "korea_challenge",
        _ => "unknown"
    };

    public async Task InitiateEnterWorldAsync(GameServerId gsId, CancellationToken cancellationToken)
    {
        CancellationTokenSource linkedCts;
        TaskCompletionSource<EnterWorldResult> tcs;

        lock (_lock)
        {
            if (_state != LoginState.Authenticated)
            {
                logger.LogWarning(
                    "Cannot enter world: invalid state {State} for connection {ConnectionId}",
                    _state, Connection.Id);
                return;
            }

            if (gameController.GetGameServer(gsId) is not { Active: true })
            {
                logger.LogWarning(
                    "Cannot enter world: game server {GsId} not available for connection {ConnectionId}",
                    gsId, Connection.Id);
                return;
            }

            _state = LoginState.EnteringWorld;
            _pendingGsId = gsId;

            _enterWorldCts = CancellationTokenSource.CreateLinkedTokenSource(
                Connection.ConnectionClosed, cancellationToken);
            _enterWorldTcs = new TaskCompletionSource<EnterWorldResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            linkedCts = _enterWorldCts;
            tcs = _enterWorldTcs;
        }

        // Send request to game server (outside the lock)
        gameController.RequestEnterWorld(Connection.AccountId, Connection.Id, gsId);

        // Spawn background task to await response and send result
        _enterWorldBackgroundTask = RunEnterWorldBackgroundAsync(tcs, linkedCts, gsId);
    }

    private async Task RunEnterWorldBackgroundAsync(TaskCompletionSource<EnterWorldResult> tcs,
        CancellationTokenSource linkedCts, GameServerId gsId)
    {
        try
        {
            var result = await tcs.Task.WaitAsync(_enterWorldTimeout, timeProvider, linkedCts.Token);

            if (result is { Success: true, Server: not null })
            {
                await Client.SendWorldCookieAsync(result.Server, CancellationToken.None);
                LogEnterWorldOutcome(LogLevel.Information, "success", gsId, null);
            }
            else
            {
                await Client.SendEnterWorldDeniedAsync(result.DenialReason, CancellationToken.None);
                LogEnterWorldOutcome(LogLevel.Information, "denied", gsId, result.DenialReason.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            LogEnterWorldOutcome(LogLevel.Debug, "cancelled", gsId, "cancelled");
        }
        catch (TimeoutException)
        {
            LogEnterWorldOutcome(LogLevel.Warning, "timeout", gsId, "timeout");
            try
            {
                await Client.SendEnterWorldDeniedAsync(0, CancellationToken.None);
            }
            catch
            {
                // Connection may be closed
            }
        }
        finally
        {
            lock (_lock)
            {
                CleanupEnterWorldState();
                if (_state == LoginState.EnteringWorld)
                {
                    _state = LoginState.Authenticated;
                }
            }
        }
    }

    private void LogEnterWorldOutcome(LogLevel level, string outcome, GameServerId gsId, string? reason)
    {
        logger.Log(level,
            "{EventName}: Enter-world {Outcome} on connection {ConnectionId} for game server {GameServerId}. " +
            "Reason {Reason}",
            EnterWorldCompletedEventName, outcome, Connection.Id.Value, gsId.Value, reason);
    }

    public void CancelEnterWorld()
    {
        lock (_lock)
        {
            if (_state != LoginState.EnteringWorld)
            {
                logger.LogDebug("CancelEnterWorld ignored: not in EnteringWorld state for connection {ConnectionId}",
                    Connection.Id);
                return;
            }

            logger.LogDebug("Cancelling EnterWorld for connection {ConnectionId}", Connection.Id);

            // Cancel the CTS to trigger the awaiting task
            _enterWorldCts?.Cancel();
        }
    }

    public void CompleteEnterWorldRequest(GameServerId gsId, byte result)
    {
        lock (_lock)
        {
            if (_state != LoginState.EnteringWorld)
            {
                logger.LogWarning(
                    "CompleteEnterWorldRequest ignored: not in EnteringWorld state for connection {ConnectionId}",
                    Connection.Id);
                return;
            }

            if (_pendingGsId != gsId)
            {
                logger.LogWarning(
                    "CompleteEnterWorldRequest ignored: gsId mismatch (expected {Expected}, got {Actual}) for connection {ConnectionId}",
                    _pendingGsId, gsId, Connection.Id);
                return;
            }

            var success = result == 0;
            GameServer? server = null;

            if (success)
            {
                server = gameController.GetGameServer(gsId);
            }

            _enterWorldTcs?.TrySetResult(new EnterWorldResult(success, server, result));
        }
    }

    public async Task DisconnectAsync()
    {
        Task? backgroundTask;

        lock (_lock)
        {
            if (_state == LoginState.Disconnected)
                return;

            logger.LogDebug("Session disconnected for connection {ConnectionId}", Connection.Id);

            // Cancel any pending enter world operation
            _enterWorldCts?.Cancel();
            CleanupEnterWorldState();

            _state = LoginState.Disconnected;
            backgroundTask = _enterWorldBackgroundTask;
        }

        if (backgroundTask is not null)
        {
            // Ignore errors during cleanup
            await backgroundTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private void CleanupEnterWorldState()
    {
        // Must be called while holding _lock
        _enterWorldCts?.Dispose();
        _enterWorldCts = null;
        _enterWorldTcs = null;
        _pendingGsId = null;
    }
}
