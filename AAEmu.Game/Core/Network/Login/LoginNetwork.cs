using System.Net;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Tasks.ServerLoad;
using NLog;

namespace AAEmu.Game.Core.Network.Login;

public class LoginNetwork : Singleton<LoginNetwork>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly Func<LoginProtocolHandler, CancellationToken, Task<Client>> _createClient;
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;
    private readonly Action<LoadTask> _scheduleLoad;
    private readonly Action<LoadTask> _cancelLoad;
    private CancellationTokenSource _lifetime;
    private Task _completion = Task.CompletedTask;
    private LoginConnection _connection;

    private LoginNetwork() : this(CreateClientAsync, TimeProvider.System, Random.Shared.NextDouble,
        task => TaskManager.Instance.Schedule(task, null, TimeSpan.FromMinutes(1)),
        task => TaskManager.Instance.Cancel(task))
    {
    }

    internal LoginNetwork(Func<LoginProtocolHandler, CancellationToken, Task<Client>> createClient,
        TimeProvider clock, Func<double> jitter, Action<LoadTask> scheduleLoad, Action<LoadTask> cancelLoad)
    {
        _createClient = createClient;
        _clock = clock;
        _jitter = jitter;
        _scheduleLoad = scheduleLoad;
        _cancelLoad = cancelLoad;
    }

    internal Task Completion
    {
        get
        {
            lock (_gate)
                return _completion;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_lifetime != null)
                return;

            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            _completion = Task.Run(() => RunAsync(lifetime));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            // The owner loop closes its own socket and cancels its own load task.
            _lifetime?.Cancel();
            _lifetime = null;
            _connection = null;
        }
    }

    public async Task StopAsync()
    {
        Task completion;
        lock (_gate)
        {
            Stop();
            completion = _completion;
        }
        await completion.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource lifetime)
    {
        var failures = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var handler = new LoginProtocolHandler(
                    connection => PublishConnection(lifetime, connection), ClearConnection,
                    _scheduleLoad, _cancelLoad);
                Client client = null;
                try
                {
                    using var timeout = new CancellationTokenSource(RegistrationTimeout, _clock);
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, timeout.Token);
                    client = await _createClient(handler, attempt.Token).ConfigureAwait(false);
                    attempt.Token.ThrowIfCancellationRequested();
                    if (!client.ConnectAsync())
                        throw new InvalidOperationException("Login connect did not start.");

                    var ready = await Task.WhenAny(handler.Registered, handler.Disconnected)
                        .WaitAsync(attempt.Token).ConfigureAwait(false);
                    if (ready == handler.Registered)
                    {
                        // TCP connect is not success: rejected registration must retain the backoff.
                        failures = 0;
                        await handler.Disconnected.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn("Login connection or registration timed out after {0} seconds", RegistrationTimeout.TotalSeconds);
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "Login connection attempt failed");
                }
                finally
                {
                    try
                    {
                        // NetCoreServer also calls OnDisconnected for refused connects, but its Dispose
                        // does not close a socket once IsConnecting and IsConnected are both false.
                        client?.Dispose();
                        client?.Socket?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        Logger.Warn(exception, "Login client cleanup failed");
                    }
                    handler.Stop();
                }

                if (lifetime.IsCancellationRequested)
                    break;
                failures = Math.Min(failures + 1, 6);
                var delay = GetRetryDelay(failures, _jitter());
                Logger.Warn("Login link unavailable. Retry in {0:F2} seconds", delay.TotalSeconds);
                await Task.Delay(delay, _clock, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_lifetime, lifetime))
                    _lifetime = null;
                lifetime.Dispose();
            }
        }
    }

    internal static TimeSpan GetRetryDelay(int failures, double jitter)
    {
        // Positive jitter keeps every wait within 1-30 seconds, including the first failure.
        var seconds = Math.Min(30, Math.Pow(2, Math.Clamp(failures - 1, 0, 5)));
        return TimeSpan.FromSeconds(Math.Min(30, seconds + Math.Clamp(jitter, 0, 1)));
    }

    private bool PublishConnection(CancellationTokenSource lifetime, LoginConnection connection)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_lifetime, lifetime) || lifetime.IsCancellationRequested)
                return false;
            _connection = connection;
            return true;
        }
    }

    private void ClearConnection(LoginConnection connection)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection))
                _connection = null;
        }
    }

    public void SetConnection(LoginConnection connection)
    {
        lock (_gate)
            _connection = connection;
    }

    public LoginConnection GetConnection()
    {
        lock (_gate)
            return _connection;
    }

    private static async Task<Client> CreateClientAsync(LoginProtocolHandler handler, CancellationToken cancellationToken)
    {
        var config = AppConfiguration.Instance.LoginNetwork;
        var addresses = await Dns.GetHostAddressesAsync(config.Host, cancellationToken).ConfigureAwait(false);
        return new LoginClient(addresses.First(), config.Port, handler);
    }
}
