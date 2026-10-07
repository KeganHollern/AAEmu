using System.Collections.Concurrent;
using System.Net;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models;

namespace AAEmu.Game.Core.Network.Connections;

public class GameConnectionTable : Singleton<GameConnectionTable>
{
    private readonly ConcurrentDictionary<uint, GameConnection> _connections;
    private readonly object _admissionLock = new();
    private bool _stopping;
    private readonly Dictionary<IPAddress, int> _addressCounts = [];
    private readonly GameNetworkLimitsConfig _limits;

    internal GameConnectionTable() : this(AppConfiguration.Instance.GameNetworkLimits) { }

    internal GameConnectionTable(GameNetworkLimitsConfig limits)
    {
        _limits = limits ?? AppConfiguration.Instance.GameNetworkLimits;
        _limits.Validate();
        _connections = new ConcurrentDictionary<uint, GameConnection>();
    }

    public bool AddConnection(GameConnection con)
    {
        lock (_admissionLock)
        {
            var address = NormalizeAddress(con.Ip);
            var count = _addressCounts.GetValueOrDefault(address);
            if (!_stopping && !con.IsClosed && _connections.Count < _limits.MaxConnections &&
                count < _limits.MaxConnectionsPerAddress && _connections.TryAdd(con.Id, con))
            {
                _addressCounts[address] = count + 1;
                return true;
            }
        }

        con.Shutdown();
        return false;
    }

    internal List<GameConnection> BeginShutdown()
    {
        lock (_admissionLock)
        {
            _stopping = true;
            var connections = GetConnections();
            foreach (var connection in connections)
                connection.MarkClosed();
            return connections;
        }
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
        lock (_admissionLock)
        {
            var connection = GetConnection(session);
            return connection != null ? RemoveConnection(connection.Id) : null;
        }
    }

    public GameConnection RemoveConnection(uint id)
    {
        lock (_admissionLock)
        {
            if (!_connections.TryRemove(id, out var con))
                return null;
            var address = NormalizeAddress(con.Ip);
            var count = _addressCounts[address] - 1;
            if (count == 0)
                _addressCounts.Remove(address);
            else
                _addressCounts[address] = count;
            return con;
        }
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

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
