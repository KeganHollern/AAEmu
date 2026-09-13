using System.Collections.Concurrent;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;

namespace AAEmu.Game.Core.Network.Connections;

public class StreamConnectionTable : Singleton<StreamConnectionTable>
{
    private readonly ConcurrentDictionary<uint, StreamConnection> _connections;

    internal StreamConnectionTable()
    {
        _connections = new ConcurrentDictionary<uint, StreamConnection>();
    }

    public bool AddConnection(StreamConnection con)
    {
        if (_connections.TryAdd(con.Id, con))
            return true;

        con.Shutdown();
        return false;
    }

    public StreamConnection GetConnection(uint id)
    {
        _connections.TryGetValue(id, out var con);
        return con;
    }

    public StreamConnection GetConnection(ISession session)
    {
        var connection = GetConnection(session.SessionId);
        return connection?.MatchesSession(session) == true ? connection : null;
    }

    public StreamConnection RemoveConnection(ISession session)
    {
        var connection = GetConnection(session);
        return connection != null && _connections.TryRemove(new KeyValuePair<uint, StreamConnection>(connection.Id, connection))
            ? connection
            : null;
    }

    public StreamConnection RemoveConnection(uint id)
    {
        _connections.TryRemove(id, out var con);
        return con;
    }

    public List<StreamConnection> GetConnections()
    {
        return [.. _connections.Values];
    }
}
