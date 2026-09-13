using System.Collections.Concurrent;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;

namespace AAEmu.Game.Core.Network.Connections;

public class GameConnectionTable : Singleton<GameConnectionTable>
{
    private readonly ConcurrentDictionary<uint, GameConnection> _connections;

    internal GameConnectionTable()
    {
        _connections = new ConcurrentDictionary<uint, GameConnection>();
    }

    public bool AddConnection(GameConnection con)
    {
        if (_connections.TryAdd(con.Id, con))
            return true;

        con.Shutdown();
        return false;
    }

    public GameConnection GetConnection(uint id)
    {
        _connections.TryGetValue(id, out var con);
        return con;
    }

    public GameConnection GetConnection(ISession session)
    {
        var connection = GetConnection(session.SessionId);
        return connection?.MatchesSession(session) == true ? connection : null;
    }

    public GameConnection RemoveConnection(ISession session)
    {
        var connection = GetConnection(session);
        return connection != null && _connections.TryRemove(new KeyValuePair<uint, GameConnection>(connection.Id, connection))
            ? connection
            : null;
    }

    public GameConnection RemoveConnection(uint id)
    {
        _connections.TryRemove(id, out var con);
        return con;
    }

    public List<GameConnection> GetConnections()
    {
        return [.. _connections.Values];
    }

    public GameConnection GetConnectionByAccount(uint accountId)
    {
        var connectionInfo = _connections.Where(c => c.Value.AccountId == accountId &&
            c.Value.IsAuthenticated && !c.Value.IsClosed).ToList();
        if (connectionInfo.Count >= 1)
            return connectionInfo[0].Value;
        return null;
    }
}
