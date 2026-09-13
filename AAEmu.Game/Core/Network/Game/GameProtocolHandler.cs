using System.Collections.Concurrent;
using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;

using NLog;

namespace AAEmu.Game.Core.Network.Game;

public class GameProtocolHandler : BaseProtocolHandler
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// List of packet handlers (level, id, class type)
    /// </summary>
    private readonly ConcurrentDictionary<byte, ConcurrentDictionary<uint, Type>> _packets;

    public GameProtocolHandler()
    {
        _packets = new ConcurrentDictionary<byte, ConcurrentDictionary<uint, Type>>();
        // For 1.2 client we only have Level1 and Level2 packets
        _packets.TryAdd(1, new ConcurrentDictionary<uint, Type>());
        _packets.TryAdd(2, new ConcurrentDictionary<uint, Type>());
    }

    /// <summary>
    /// On connect event
    /// </summary>
    /// <param name="session"></param>
    public override void OnConnect(ISession session)
    {
        Logger.Info($"Connect from {session.Ip} established, session id: {session.SessionId}");
        try
        {
            var con = new GameConnection(session);
            if (!GameConnectionTable.Instance.AddConnection(con))
                return;
            con.OnConnect();
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }
    }

    /// <summary>
    /// On disconnect event
    /// </summary>
    /// <param name="session"></param>
    public override void OnDisconnect(ISession session)
    {
        var connection = GameConnectionTable.Instance.GetConnection(session);
        if (connection == null)
            return;
        connection.MarkClosed();
        // NetCoreServer can call OnDisconnected while it owns its send lock.
        // An active packet can need that lock, so do not wait here for its session lock.
        if (Monitor.IsEntered(connection.SessionSyncRoot) || !Monitor.TryEnter(connection.SessionSyncRoot))
        {
            _ = Task.Run(() => DisconnectWhenIdle(connection, session));
            return;
        }
        try { DisconnectWhenIdle(connection, session); }
        finally { Monitor.Exit(connection.SessionSyncRoot); }
    }

    private static void DisconnectWhenIdle(GameConnection connection, ISession session)
    {
        lock (connection.SessionSyncRoot)
        {
            try
            {
                try { connection.OnDisconnect(); }
                finally
                {
                    try
                    {
                        if (connection.IsAuthenticated && connection.AccountId > 0)
                            StreamManager.Instance.RemoveToken(connection);
                    }
                    finally
                    {
                        try { ReconnectTokenManager.Instance.Remove(connection); }
                        finally { GameConnectionTable.Instance.RemoveConnection(session); }
                    }
                }
            }
            catch (Exception exception) { Logger.Error(exception, "Disconnect failed for session {SessionId}", session.SessionId); }
        }
        Logger.Info("Client from {RemoteIp} disconnected", session.Ip);
    }

    /// <summary>
    /// Handle incoming data for session
    /// </summary>
    /// <param name="session"></param>
    /// <param name="buf"></param>
    /// <param name="offset"></param>
    /// <param name="bytes"></param>
    public override void OnReceive(ISession session, byte[] buf, int offset, int bytes)
    {
        try
        {
            var connection = GameConnectionTable.Instance.GetConnection(session);
            if (connection == null)
            {
                Logger.Error($"{nameof(OnReceive)}: connection for session id {session.SessionId} is null");
                return;
            }

            OnReceive(connection, buf, offset, bytes);
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }
    }

    /// <summary>
    /// Handle incoming data for GameConnection
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="buf"></param>
    /// <param name="offset"></param>
    /// <param name="bytes"></param>
    public void OnReceive(GameConnection connection, byte[] buf, int offset, int bytes)
    {
        lock (connection.SessionSyncRoot)
            ReceiveLocked(connection, buf, offset, bytes);
    }

    private void ReceiveLocked(GameConnection connection, byte[] buf, int offset, int bytes)
    {
        try
        {
            var stream = new PacketStream();
            if (connection.LastPacket != null)
            {
                stream.Insert(0, connection.LastPacket);
                connection.LastPacket = null;
            }
            stream.Insert(stream.Count, buf, offset, bytes);
            while (stream is { Count: > 0 })
            {
                ushort len;
                try
                {
                    len = stream.ReadUInt16();
                }
                catch (MarshalException)
                {
                    //Logger.Warn("Error on reading type {0}", type);
                    stream.Rollback();
                    connection.LastPacket = stream;
                    stream = null;
                    continue;
                }
                var packetLen = len + stream.Pos;
                if (packetLen <= stream.Count)
                {
                    stream.Rollback();
                    var stream2 = new PacketStream();
                    stream2.Replace(stream, 0, packetLen);
                    if (stream.Count > packetLen)
                    {
                        var stream3 = new PacketStream();
                        stream3.Replace(stream, packetLen, stream.Count - packetLen);
                        stream = stream3;
                    }
                    else
                        stream = null;
                    stream2.ReadUInt16(); //len
                    stream2.ReadByte(); //unk
                    var level = stream2.ReadByte();

                    //byte crc = 0;
                    //byte counter = 0;
                    if (level == 1)
                    {
                        _ = stream2.ReadByte(); // TODO: verify 1.2 crc
                        _ = stream2.ReadByte(); // TODO: verify 1.2 counter
                    }

                    var type = stream2.ReadUInt16();
                    if (!CanDispatch(connection, type, level))
                    {
                        Logger.Warn("Rejected game packet {PacketOpcode} at level {PacketLevel} in state {GameState} on connection {ConnectionId}", type, level, connection.State, connection.Id);
                        connection.Shutdown();
                        return;
                    }
                    _packets[level].TryGetValue(type, out var classType);
                    if (classType == null)
                    {
                        HandleUnknownPacket(connection, type, level, stream2);
                    }
                    else
                    {
                        var packet = (GamePacket)Activator.CreateInstance(classType);
                        packet!.Level = level;
                        packet.Connection = connection;
                        packet.Decode(stream2);
                    }
                }
                else
                {
                    stream.Rollback();
                    connection.LastPacket = stream;
                    stream = null;
                }
            }
        }
        catch (Exception e)
        {
            connection?.Shutdown();
            Logger.Error(e, "Game protocol failed on connection {0} from {1}", connection?.Id, connection?.Ip);
        }
    }

    /// <summary>
    /// Registers a GamePacket handler by Id and Level
    /// </summary>
    /// <param name="type"></param>
    /// <param name="level"></param>
    /// <param name="classType"></param>
    public void RegisterPacket(uint type, byte level, Type classType)
    {
        _packets[level][type] = classType;
    }

    internal static bool CanDispatch(GameConnection connection, uint type, byte level)
    {
        if (connection.IsClosed || level is not (1 or 2))
            return false;
        var authenticated = connection.IsAuthenticated && connection.AccountId > 0;
        if (!authenticated)
            return connection.State == GameState.Connected && connection.AccountId == 0 &&
                connection.ActiveChar == null && level == 1 && type == CSOffsets.X2EnterWorldPacket;
        if (connection.State == GameState.Connected)
            return false;

        // CryNetwork transport messages are independent of lobby/world gameplay state.
        if (level == 2)
            return type <= 0x16 && type != 3;
        if (type == CSOffsets.X2EnterWorldPacket)
            return false;

        var lobbyPacket = type is CSOffsets.CSListCharacterPacket or CSOffsets.CSRefreshInCharacterListPacket or
            CSOffsets.CSCreateCharacterPacket or CSOffsets.CSEditCharacterPacket or CSOffsets.CSDeleteCharacterPacket or
            CSOffsets.CSSelectCharacterPacket or CSOffsets.CSCancelCharacterDeletePacket;
        if (lobbyPacket)
            return connection.State == GameState.Lobby && connection.ActiveChar == null;

        // Account/UI requests also occur before a character enters the world.
        if (type is CSOffsets.CSRequestUIDataPacket or CSOffsets.CSRestrictCheckPacket or
            CSOffsets.CSRequestSecondPasswordKeyTablesPacket or CSOffsets.CSSetupSecondPassword or
            CSOffsets.CSResturnAddrsPacket or CSOffsets.CSSetLpManageCharacterPacket)
            return true;
        if (type == CSOffsets.CSLeaveWorldPacket)
            return connection.State is GameState.Lobby or GameState.World;

        if (connection.ActiveChar?.AccountId != connection.AccountId)
            return false;
        return type switch
        {
            CSOffsets.CSSpawnCharacterPacket => connection.State == GameState.CharacterSelected,
            CSOffsets.CSNotifyInGamePacket => connection.State is GameState.EnteringWorld or GameState.World,
            CSOffsets.CSNotifyInGameCompletedPacket => connection.State == GameState.World,
            CSOffsets.CSInstanceLoadedPacket or CSOffsets.CSNotifySubZonePacket => connection.State is GameState.CharacterSelected or GameState.EnteringWorld or GameState.World,
            _ => connection.State == GameState.World
        };
    }

    /// <summary>
    /// Handle and Log unknown packet data
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="type"></param>
    /// <param name="level"></param>
    /// <param name="stream"></param>
    private static void HandleUnknownPacket(GameConnection connection, uint type, byte level, PacketStream stream)
    {
        if (!connection.UnknownPacketEvents.TryConsume())
            return;

        Logger.Warn(
            "{EventName}: Rejected unknown {Network} packet 0x{PacketOpcode:X4} at level {PacketLevel} with " +
            "{PacketLength} unread bytes on connection {ConnectionId} from {RemoteIp}",
            "game.packet.rejected", "game", type, level, stream.Count - stream.Pos, connection.Id, connection.Ip);
    }
}
