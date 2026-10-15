using System.Net;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Stream;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Services.Security;

namespace AAEmu.Game.Core.Network.Connections;

public class StreamConnection(ISession session, GameSecurityMetrics securityMetrics = null)
{
    private int _requestId = -1;
    private int _disconnectRecorded;
    private readonly Dictionary<int, Doodad[]> _requests = [];

    internal GameSecurityMetrics SecurityMetrics { get; } = securityMetrics ?? GameSecurityMetrics.Default;

    public uint Id => session.SessionId;
    internal bool MatchesSession(ISession candidate) => ReferenceEquals(session, candidate);
    public IPAddress Ip => session.Ip;
    public GameConnection GameConnection { get; set; }
    public PacketStream LastPacket { get; set; }
    internal ConnectionEventLimiter UnknownPacketEvents { get; } = new();
    internal ConnectionEventLimiter TokenFailureEvents { get; } = new();

    public int GetNextRequestId(Doodad[] doodads)
    {
        Interlocked.Increment(ref _requestId);
        _requests.Add(_requestId, doodads);
        return _requestId;
    }

    public Doodad[] GetRequest(int requestId)
    {
        if (_requests.TryGetValue(requestId, out var request))
            return request;
        return null;
    }

    public void RemoveRequest(int requestId)
    {
        _requests.Remove(requestId);
    }

    public void SendPacket(StreamPacket packet)
    {
        SendPacket(packet.Encode());
    }

    public void SendPacket(byte[] packet)
    {
        session?.SendPacket(packet);
    }

    public static void OnConnect()
    {
    }

    internal void RecordDisconnect(SecurityDisconnectReason reason)
    {
        if (Interlocked.Exchange(ref _disconnectRecorded, 1) == 0)
            GameSecurityTelemetry.Disconnected(SecurityNetwork.Stream, reason, Id, Ip, SecurityMetrics);
    }

    public void Shutdown() => Shutdown(SecurityDisconnectReason.ServerClose);

    public void Shutdown(SecurityDisconnectReason reason)
    {
        RecordDisconnect(reason);
        session?.Close();
    }
}
