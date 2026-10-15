using System.Net;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Packets.G2L;
using AAEmu.Game.Models;
using AAEmu.Game.Services.Security;

namespace AAEmu.Game.Core.Network.Connections;

public class LoginConnection(ISession session, Action onRegistered = null, GameSecurityMetrics securityMetrics = null)
{
    private int _disconnectRecorded;

    internal GameSecurityMetrics SecurityMetrics { get; } = securityMetrics ?? GameSecurityMetrics.Default;

    public uint Id => session.SessionId;
    public IPAddress Ip => session.Ip;

    public bool Block { get; set; }
    public bool IsRegistered { get; set; }
    public PacketStream LastPacket { get; set; }
    internal ConnectionEventLimiter UnknownPacketEvents { get; } = new();
    internal ConnectionEventLimiter TokenFailureEvents { get; } = new();

    internal void Register()
    {
        if (IsRegistered || Block)
            return;
        IsRegistered = true;
        onRegistered?.Invoke();
    }

    public void OnConnect()
    {
        var secretKey = AppConfiguration.Instance.SecretKey;
        var gsId = AppConfiguration.Instance.Id;
        var additionalesGsId = AppConfiguration.Instance.AdditionalesId;
        SendPacket(new GLRegisterGameServerPacket(secretKey, gsId, additionalesGsId));
    }

    public void SendPacket(LoginPacket packet)
    {
        if (Block)
            return;
        packet.Connection = this;
        byte[] buf = packet.Encode();
        session.SendPacket(buf);
    }

    internal void RecordDisconnect(SecurityDisconnectReason reason)
    {
        if (Interlocked.Exchange(ref _disconnectRecorded, 1) == 0)
            GameSecurityTelemetry.Disconnected(SecurityNetwork.Login, reason, Id, Ip, SecurityMetrics);
    }

    public void Close() => Close(SecurityDisconnectReason.ServerClose);

    public void Close(SecurityDisconnectReason reason)
    {
        RecordDisconnect(reason);
        session.Close();
    }
}
