using System.Collections.Concurrent;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Services.Security;
using NLog;

namespace AAEmu.Game.Core.Network.Stream;

public class StreamProtocolHandler : BaseProtocolHandler
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<uint, Type> _packets = new();

    public override void OnConnect(ISession session)
    {
        Logger.Info("Connect from {0} established, session id: {1}", session.Ip.ToString(), session.SessionId.ToString());
        try
        {
            var con = new StreamConnection(session);
            if (!StreamConnectionTable.Instance.AddConnection(con))
                return;
            StreamConnection.OnConnect();
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }
    }

    public override void OnDisconnect(ISession session)
    {
        try
        {
            var con = StreamConnectionTable.Instance.RemoveConnection(session);
            if (con != null)
            {
                con.RecordDisconnect(SecurityDisconnectReason.RemoteClose);
                AAEmu.Game.Core.Managers.Stream.UccManager.Instance.RemoveConnection(con);
            }
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }

        Logger.Info("Client from {0} disconnected", session.Ip.ToString());
    }

    public override void OnReceive(ISession session, byte[] buf, int offset, int bytes)
    {
        try
        {
            var connection = StreamConnectionTable.Instance.GetConnection(session);
            if (connection == null)
                return;
            OnReceive(connection, buf, offset, bytes);
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }
    }

    public void OnReceive(StreamConnection connection, byte[] buf, int offset, int bytes)
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
            while (stream != null && stream.Count > 0)
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
                    var type = stream2.ReadUInt16();
                    _packets.TryGetValue(type, out var classType);
                    if (classType == null)
                    {
                        HandleUnknownPacket(connection, type, stream2);
                    }
                    else
                    {
                        var packet = (StreamPacket)Activator.CreateInstance(classType);
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
            if (connection != null)
            {
                GameSecurityTelemetry.PacketRejected(SecurityNetwork.Stream,
                    e is MarshalException ? SecurityPacketRejection.Malformed : SecurityPacketRejection.HandlerError,
                    connection.UnknownPacketEvents.TryConsume(), connection.Id, connection.Ip, metrics: connection.SecurityMetrics);
                connection.Shutdown(SecurityDisconnectReason.ProtocolError);
            }
            Logger.Error(e, "Stream protocol failed on connection {0} from {1}", connection?.Id, connection?.Ip);
        }
    }

    public void RegisterPacket(uint type, Type classType)
    {
        if (_packets.ContainsKey(type))
            _packets.TryRemove(type, out _);
        _packets.TryAdd(type, classType);
    }

    private static void HandleUnknownPacket(StreamConnection connection, uint type, PacketStream stream)
    {
        GameSecurityTelemetry.PacketRejected(SecurityNetwork.Stream, SecurityPacketRejection.UnknownOpcode,
            connection.UnknownPacketEvents.TryConsume(), connection.Id, connection.Ip, type,
            length: stream.Count - stream.Pos, metrics: connection.SecurityMetrics);
    }
}
