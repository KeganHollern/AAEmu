using System.Security.Cryptography;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.S2C;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Core.Managers.World;

public class StreamManager : Singleton<StreamManager>, IStreamManager
{
    private readonly object _tokensLock = new();
    private readonly Dictionary<uint, GameConnection> _tokens = [];
    private readonly GameConnectionTable _connections;
    private readonly Func<uint> _nextToken;

    public StreamManager() : this(GameConnectionTable.Instance,
        () => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint))))
    {
    }

    internal StreamManager(GameConnectionTable connections, Func<uint> nextToken)
    {
        _connections = connections;
        _nextToken = nextToken;
    }

    public static void Load()
    {
        // TODO ...
    }

    public uint AddToken(GameConnection connection)
    {
        lock (connection.SessionSyncRoot)
        lock (_tokensLock)
        {
            if (!IsCurrent(connection))
                throw new InvalidOperationException("Stream tokens need a current authenticated game connection.");

            foreach (var entry in _tokens)
                if (ReferenceEquals(entry.Value, connection))
                    return entry.Key;

            for (var attempt = 0; attempt < 256; attempt++)
            {
                var token = _nextToken();
                if (token != 0 && _tokens.TryAdd(token, connection))
                    return token;
            }
            throw new InvalidOperationException("Could not allocate a unique stream token.");
        }
    }

    public void RemoveToken(GameConnection connection)
    {
        lock (_tokensLock)
        {
            foreach (var entry in _tokens.Where(entry => ReferenceEquals(entry.Value, connection)).ToArray())
                _tokens.Remove(entry.Key);
        }
    }

    public void Login(StreamConnection connection, uint accountId, uint token)
    {
        connection.SendPacket(new TCJoinResponsePacket(TryJoin(connection, accountId, token) ? (byte)0 : (byte)1));
    }

    internal bool TryJoin(StreamConnection connection, uint accountId, uint token)
    {
        GameConnection gameConnection;
        lock (_tokensLock)
        {
            if (accountId == 0 || token == 0 || !_tokens.TryGetValue(token, out gameConnection))
                return false;
        }

        lock (gameConnection.SessionSyncRoot)
        lock (_tokensLock)
        {
            if (!_tokens.TryGetValue(token, out var current) || !ReferenceEquals(current, gameConnection) ||
                !IsCurrent(gameConnection) || gameConnection.AccountId != accountId ||
                connection.GameConnection != null && !ReferenceEquals(connection.GameConnection, gameConnection))
                return false;

            connection.GameConnection = gameConnection;
            return true;
        }
    }

    private bool IsCurrent(GameConnection connection) => connection.IsAuthenticated && !connection.IsClosed &&
        connection.AccountId != 0 && ReferenceEquals(_connections.GetConnection(connection.Id), connection);

    public static void RequestCell(StreamConnection connection, uint instanceId, int x, int y)
    {
        if (connection is not null)
        {
            var worldInstanceId = connection.GameConnection?.ActiveChar?.Transform?.InstanceId ?? WorldManager.DefaultInstanceId;
            if (worldInstanceId != instanceId)
            {
                // Trying to grab cell info of a instance the player is not inside of
            }
            var world = WorldManager.Instance.GetWorld(instanceId);
            // TODO: Handle requests for instances correctly ?
            var doodads = world.GetInCell<Doodad>(x, y).ToArray();
            var requestId = connection.GetNextRequestId(doodads);
            var count = Math.Min(doodads.Length, 30);
            var res = new Doodad[count];
            Array.Copy(doodads, 0, res, 0, count);
            connection.SendPacket(new TCDoodadStreamPacket(requestId, count, res));
        }
    }

    public static void ContinueCell(StreamConnection connection, int requestId, int next)
    {
        var doodads = connection.GetRequest(requestId);
        if (doodads == null)
            return;
        if (next >= doodads.Length)
            connection.RemoveRequest(requestId);
        var count = Math.Min(doodads.Length - next, 30);
        var res = new Doodad[count];
        Array.Copy(doodads, next > 0 ? next - 1 : 0, res, 0, count);
        next += count;
        connection.SendPacket(new TCDoodadStreamPacket(requestId, next, res));
    }
}
