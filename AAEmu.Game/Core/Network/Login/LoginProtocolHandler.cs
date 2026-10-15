using System.Collections.Concurrent;
using System.Globalization;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.L2G;
using AAEmu.Game.Models.Tasks.ServerLoad;
using AAEmu.Game.Services.Security;
using NLog;

namespace AAEmu.Game.Core.Network.Login;

public class LoginProtocolHandler : BaseProtocolHandler
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<uint, Type> _packets = new();
    private readonly object _gate = new();
    private readonly Func<LoginConnection, bool> _publish;
    private readonly Action<LoginConnection> _clear;
    private readonly Action<LoadTask> _scheduleLoad;
    private readonly Action<LoadTask> _cancelLoad;
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte[]> _earlyFrames = [];
    private LoginConnection _connection;
    private ISession _session;
    private LoadTask _loadTask;
    private bool _stopped;

    internal Task Registered => _registered.Task;
    internal Task Disconnected => _disconnected.Task;

    public LoginProtocolHandler() : this(_ => true, _ => { }, _ => { }, _ => { })
    {
    }

    internal LoginProtocolHandler(Func<LoginConnection, bool> publish, Action<LoginConnection> clear,
        Action<LoadTask> scheduleLoad, Action<LoadTask> cancelLoad)
    {
        _publish = publish;
        _clear = clear;
        _scheduleLoad = scheduleLoad;
        _cancelLoad = cancelLoad;
        RegisterPacket(LGOffsets.LGRegisterGameServerPacket, typeof(LGRegisterGameServerPacket));
        RegisterPacket(LGOffsets.LGPlayerEnterPacket, typeof(LGPlayerEnterPacket));
        RegisterPacket(LGOffsets.LGPlayerReconnectPacket, typeof(LGPlayerReconnectPacket));
        RegisterPacket(LGOffsets.LGRequestInfoPacket, typeof(LGRequestInfoPacket));
        RegisterPacket(LGOffsets.LGModerationResultPacket, typeof(LGModerationResultPacket));
        RegisterPacket(LGOffsets.LGModerationStatePacket, typeof(LGModerationStatePacket));
    }

    public override void OnConnect(ISession session)
    {
        lock (_gate)
        {
            if (_stopped || (_session != null && !ReferenceEquals(_session, session)))
            {
                session.Close();
                return;
            }
            if (_connection != null)
                return;

            _session = session;
            _connection = new LoginConnection(session, OnRegistered);
            if (!_publish(_connection))
            {
                Stop();
                session.Close();
                return;
            }

            Logger.Info("Connect to {0} established, session id: {1}", session.Ip, session.SessionId.ToString(CultureInfo.InvariantCulture));
            _connection.OnConnect();
            // NetCoreServer 8.0.7 starts receiving before it calls OnConnected.
            foreach (var frame in _earlyFrames.ToArray())
            {
                if (_stopped)
                    break;
                OnReceive(_connection, frame, 0, frame.Length);
            }
            _earlyFrames.Clear();
        }
    }

    private void OnRegistered()
    {
        lock (_gate)
        {
            if (_stopped || _registered.Task.IsCompleted)
                return;
            _loadTask = new LoadTask(_connection);
            _scheduleLoad(_loadTask);
            _registered.TrySetResult();
        }
    }

    public override void OnDisconnect(ISession session)
    {
        lock (_gate)
        {
            if (_session != null && !ReferenceEquals(_session, session))
                return;
            _connection?.RecordDisconnect(SecurityDisconnectReason.RemoteClose);
            Logger.Info("Connection to LoginServer has been lost");
            Stop();
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _earlyFrames.Clear();
            if (_connection != null)
            {
                _connection.RecordDisconnect(SecurityDisconnectReason.ServerClose);
                _connection.Block = true;
                _connection.LastPacket = null;
                _clear(_connection);
            }
            if (_loadTask != null)
            {
                _cancelLoad(_loadTask);
                _loadTask = null;
            }
            _disconnected.TrySetResult();
        }
    }

    public override void OnReceive(ISession session, byte[] buf, int offset, int bytes)
    {
        lock (_gate)
        {
            if (_stopped || (_session != null && !ReferenceEquals(_session, session)))
                return;
            _session = session;
            if (_connection == null)
            {
                _earlyFrames.Add(buf.AsSpan(offset, bytes).ToArray());
                return;
            }
            OnReceive(_connection, buf, offset, bytes);
        }
    }

    public void OnReceive(LoginConnection connection, byte[] buf, int offset, int bytes)
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
            while (stream != null && stream.Count > 0 && !connection.Block)
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
                        var packet = (LoginPacket)Activator.CreateInstance(classType);
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
        catch (MarshalException e)
        {
            GameSecurityTelemetry.PacketRejected(SecurityNetwork.Login, SecurityPacketRejection.Malformed,
                connection.UnknownPacketEvents.TryConsume(), connection.Id, connection.Ip, metrics: connection.SecurityMetrics);
            connection.Close(SecurityDisconnectReason.ProtocolError);
            Logger.Error(e, "Login protocol failed on connection {0} from {1}", connection.Id, connection.Ip);
        }
    }

    public void RegisterPacket(uint type, Type classType)
    {
        if (_packets.ContainsKey(type))
            _packets.TryRemove(type, out _);

        _packets.TryAdd(type, classType);
    }

    private static void HandleUnknownPacket(LoginConnection connection, uint type, PacketStream stream)
    {
        GameSecurityTelemetry.PacketRejected(SecurityNetwork.Login, SecurityPacketRejection.UnknownOpcode,
            connection.UnknownPacketEvents.TryConsume(), connection.Id, connection.Ip, type,
            length: stream.Count - stream.Pos, metrics: connection.SecurityMetrics);
    }
}
