using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Packets.G2L;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;
using NLog;

namespace AAEmu.Game.Core.Managers;

public class ModerationManager : Singleton<ModerationManager>, IModerationManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IPermissionManager _permissions;
    private readonly Func<ModerationRequest, bool> _send;
    private readonly Func<IReadOnlyList<GameConnection>> _connections;
    private readonly Action<GameConnection, string> _kick;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<uint, ModerationState> _states = [];
    private readonly ConcurrentDictionary<ulong, PendingRequest> _pending = new();
    private sealed record PendingRequest(uint AccountId, TaskCompletionSource<ModerationResult> Completion);

    public ModerationManager(IPermissionManager permissions)
        : this(permissions, SendToLogin, () => GameConnectionTable.Instance.GetConnections(),
            (connection, reason) => connection.Kick(reason), TimeProvider.System, TimeSpan.FromSeconds(10))
    {
    }

    internal ModerationManager(IPermissionManager permissions, Func<ModerationRequest, bool> send,
        Func<IReadOnlyList<GameConnection>> connections, Action<GameConnection, string> kick,
        TimeProvider time, TimeSpan timeout)
    {
        _permissions = permissions;
        _send = send;
        _connections = connections;
        _kick = kick;
        _time = time;
        _timeout = timeout;
    }

    private static bool SendToLogin(ModerationRequest request)
    {
        var connection = LoginNetwork.Instance.GetConnection();
        if (connection is not { IsRegistered: true, Block: false })
            return false;
        connection.SendPacket(new GLModerationRequestPacket(request));
        return true;
    }

    public Task<ModerationResult> ModerateAsync(Character actor, ModerationTarget target,
        ModerationAction action, ulong durationSeconds, string reason)
    {
        // Authorization belongs to dispatch time. An authorized request may complete
        // after a later role change; Login records the original actor and request.
        if (actor == null || target == null || action == ModerationAction.ReadState ||
            !_permissions.CanUse(actor, GamePermission.ModerateAccounts) ||
            !_permissions.CanModerate(actor.AccountId, target.AccountId))
            return Task.FromResult(Failure(0, target?.AccountId ?? 0, ModerationStatus.InvalidRequest));

        return SendRequestAsync(new ModerationRequest(NewRequestId(), actor.AccountId, actor.Id,
            target.AccountId, action, durationSeconds, reason));
    }

    public async Task<bool> RefreshAccountAsync(uint accountId)
    {
        if (accountId == 0)
            return false;
        var result = await SendRequestAsync(new ModerationRequest(NewRequestId(), 0, 0,
            accountId, ModerationAction.ReadState, 0, string.Empty));
        return result.Status == ModerationStatus.Success;
    }

    public async Task RefreshLiveAccountsAsync()
    {
        foreach (var accountId in _connections().Where(connection => connection.IsAuthenticated)
                     .Select(connection => connection.AccountId).Distinct())
        {
            if (!await RefreshAccountAsync(accountId))
            {
                // A reconnect must not keep a session whose current ban/mute state is unknown.
                foreach (var connection in _connections().Where(connection => connection.AccountId == accountId))
                    DisconnectSafely(connection, "The account status could not be checked. Please reconnect.");
            }
        }
    }

    private async Task<ModerationResult> SendRequestAsync(ModerationRequest request)
    {
        if (!request.IsValid())
            return Failure(request.RequestId, request.TargetAccountId, ModerationStatus.InvalidRequest);
        var completion = new TaskCompletionSource<ModerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, new PendingRequest(request.TargetAccountId, completion)))
            return Failure(request.RequestId, request.TargetAccountId, ModerationStatus.RequestConflict);
        try
        {
            if (!_send(request))
                return Failure(request.RequestId, request.TargetAccountId, ModerationStatus.Unavailable);
            return await completion.Task.WaitAsync(_timeout);
        }
        catch (TimeoutException)
        {
            return Failure(request.RequestId, request.TargetAccountId, ModerationStatus.Unavailable);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Moderation request {RequestId} failed for account {AccountId}",
                request.RequestId, request.TargetAccountId);
            return Failure(request.RequestId, request.TargetAccountId, ModerationStatus.Unavailable);
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    public void CompleteRequest(ModerationResult result)
    {
        if (!_pending.TryGetValue(result.RequestId, out var pending) || pending.AccountId != result.State.AccountId)
            return;
        if (result.Status == ModerationStatus.Success)
            ApplyState(result.State);
        pending.Completion.TrySetResult(result);
    }

    public void ApplyState(ModerationState state)
    {
        if (state.AccountId == 0)
            return;
        lock (_stateLock)
        {
            if (_states.TryGetValue(state.AccountId, out var current) && current.Revision > state.Revision)
                return;
            _states[state.AccountId] = state;
            if (!state.IsBanned(_time.GetUtcNow()))
                return;
            foreach (var connection in _connections().Where(connection => connection.AccountId == state.AccountId))
                DisconnectSafely(connection, "This account is banned.");
        }
    }

    public bool TryAdmit(uint accountId, Action admit)
    {
        lock (_stateLock)
        {
            if (accountId == 0 || !_states.TryGetValue(accountId, out var state) || state.IsBanned(_time.GetUtcNow()))
                return false;
            admit();
            return true;
        }
    }

    public bool CanChat(uint accountId)
    {
        lock (_stateLock)
            return accountId > 0 && _states.TryGetValue(accountId, out var state) &&
                !state.IsMuted(_time.GetUtcNow()) && !state.IsBanned(_time.GetUtcNow());
    }

    public bool TryKick(Character actor, ModerationTarget target, string reason, out string error)
    {
        error = string.Empty;
        if (actor == null || target == null || !_permissions.CanUse(actor, GamePermission.ModerateAccounts) ||
            !_permissions.CanModerate(actor.AccountId, target.AccountId))
        {
            error = "You cannot moderate this account.";
            return false;
        }
        var connections = _connections().Where(connection => connection.AccountId == target.AccountId).ToList();
        if (connections.Count == 0)
        {
            error = "The target account is not connected.";
            return false;
        }
        var succeeded = true;
        foreach (var connection in connections)
            succeeded &= DisconnectSafely(connection, reason);
        if (!succeeded)
            error = "The connection closed, but cleanup reported an error. Check the server log.";
        return succeeded;
    }

    private bool DisconnectSafely(GameConnection connection, string reason)
    {
        try
        {
            _kick(connection, reason);
            return true;
        }
        catch (Exception exception)
        {
            connection.Shutdown();
            Logger.Error(exception, "Moderation disconnect cleanup failed for account {AccountId}", connection.AccountId);
            return false;
        }
    }

    public bool TryResolveTarget(string value, out ModerationTarget target)
    {
        target = null;
        if (value.StartsWith("account:", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(value.AsSpan(8), out var accountId) || accountId == 0)
                return false;
            target = new ModerationTarget(accountId, 0, value);
            return true;
        }

        var numeric = uint.TryParse(value, out var characterId);
        var live = numeric ? WorldManager.Instance.GetCharacterById(characterId) : WorldManager.Instance.GetCharacter(value);
        if (live != null && live.AccountId > 0)
        {
            target = new ModerationTarget(live.AccountId, live.Id, live.Name);
            return true;
        }
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = numeric
            ? "SELECT account_id, id, name FROM characters WHERE id=@target AND deleted=0 LIMIT 1"
            : "SELECT account_id, id, name FROM characters WHERE name=@target AND deleted=0 LIMIT 1";
        command.Parameters.AddWithValue("@target", numeric ? (object)characterId : value);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetUInt32("account_id") == 0)
            return false;
        target = new ModerationTarget(reader.GetUInt32("account_id"), reader.GetUInt32("id"), reader.GetString("name"));
        return true;
    }

    private static ulong NewRequestId()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        } while (value == 0);
        return value;
    }

    private static ModerationResult Failure(ulong requestId, uint accountId, ModerationStatus status)
        => new(requestId, status, new ModerationState(accountId, 0, false, 0, false, 0));
}
