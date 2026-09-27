using System.Net;
using AAEmu.Commons.Network.Core;

namespace AAEmu.Game.Core.Network.Login;

/// <summary>Serializes NetCoreServer callbacks and socket disposal for this outbound link.</summary>
internal sealed class LoginClient(IPAddress address, int port, LoginProtocolHandler handler) : Client(address, port, handler)
{
    private readonly object _gate = new();

    public override bool ConnectAsync()
    {
        lock (_gate)
            return base.ConnectAsync();
    }

    public override bool Disconnect()
    {
        lock (_gate)
            return base.Disconnect();
    }

    protected override void OnConnected()
    {
        lock (_gate)
        {
            if (IsConnected)
                base.OnConnected();
        }
    }

    protected override void OnDisconnected()
    {
        lock (_gate)
            base.OnDisconnected();
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        lock (_gate)
            base.OnReceived(buffer, offset, size);
    }
}
