using System.Security.Cryptography;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Managers.World;

public sealed class ReconnectTokenManager : Singleton<ReconnectTokenManager>
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly object _tokensLock = new();
    private readonly Dictionary<uint, PendingToken> _tokens = [];
    private readonly GameConnectionTable _connections;
    private readonly TimeProvider _timeProvider;
    private readonly Func<uint> _nextToken;

    public ReconnectTokenManager() : this(GameConnectionTable.Instance, TimeProvider.System,
        () => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint))))
    {
    }

    internal ReconnectTokenManager(GameConnectionTable connections, TimeProvider timeProvider, Func<uint> nextToken)
    {
        _connections = connections;
        _timeProvider = timeProvider;
        _nextToken = nextToken;
    }

    public uint Issue(GameConnection connection)
    {
        lock (connection.SessionSyncRoot)
        lock (_tokensLock)
        {
            if (!IsCurrent(connection))
                throw new InvalidOperationException("Reconnect tokens need a current authenticated lobby connection.");

            var now = _timeProvider.GetUtcNow();
            foreach (var entry in _tokens.Where(entry => entry.Value.ExpiresAt <= now ||
                ReferenceEquals(entry.Value.Connection, connection)).ToArray())
                _tokens.Remove(entry.Key);

            for (var attempt = 0; attempt < 256; attempt++)
            {
                var token = _nextToken();
                if (token != 0 && _tokens.TryAdd(token, new PendingToken(connection, now + Lifetime)))
                    return token;
            }
            throw new InvalidOperationException("Could not allocate a unique reconnect token.");
        }
    }

    public bool Acknowledge(uint token)
    {
        PendingToken pending;
        lock (_tokensLock)
        {
            if (token == 0 || !_tokens.TryGetValue(token, out pending))
                return false;
        }

        lock (pending.Connection.SessionSyncRoot)
        lock (_tokensLock)
        {
            if (!_tokens.TryGetValue(token, out var current) || !ReferenceEquals(current, pending))
                return false;

            _tokens.Remove(token);
            if (pending.ExpiresAt <= _timeProvider.GetUtcNow() || !IsCurrent(pending.Connection))
                return false;

            pending.Connection.SendPacket(new SCReconnectAuthPacket(token));
            return true;
        }
    }

    public void Remove(GameConnection connection)
    {
        lock (_tokensLock)
        {
            foreach (var entry in _tokens.Where(entry => ReferenceEquals(entry.Value.Connection, connection)).ToArray())
                _tokens.Remove(entry.Key);
        }
    }

    private bool IsCurrent(GameConnection connection) => connection.IsAuthenticated && !connection.IsClosed &&
        connection.AccountId != 0 && connection.State == GameState.Lobby &&
        ReferenceEquals(_connections.GetConnection(connection.Id), connection);

    private sealed record PendingToken(GameConnection Connection, DateTimeOffset ExpiresAt);
}
