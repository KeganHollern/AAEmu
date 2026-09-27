using System.Net;
using AAEmu.Commons.Network.Core;

namespace AAEmu.Game.Core.Network.Login;

/// <summary>Serializes NetCoreServer callbacks and socket disposal for this outbound link.</summary>
internal sealed class LoginClient(IPAddress address, int port, LoginProtocolHandler handler) : Client(address, port, handler)
{
    private readonly object _gate = new();
    private bool _retired;

    public override bool ConnectAsync()
    {
        lock (_gate)
            return !_retired && base.ConnectAsync();
    }

    public override bool Disconnect()
    {
        lock (_gate)
            return base.Disconnect();
    }

    protected override void Dispose(bool disposingManagedResources)
    {
        lock (_gate)
        {
            // ProcessConnect clears IsConnecting before it sets IsConnected. Retirement
            // must remain visible even if Dispose observes both flags as false.
            _retired = true;
            try
            {
                base.Dispose(disposingManagedResources);
            }
            finally
            {
                // NetCoreServer does not close the socket after a refused connection,
                // or when disposal falls inside that ProcessConnect state transition.
                if (disposingManagedResources)
                    Socket?.Dispose();
            }
        }
    }

    protected override void OnConnected()
    {
        lock (_gate)
        {
            if (!_retired && IsConnected)
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
        {
            if (!_retired)
                base.OnReceived(buffer, offset, size);
        }
    }
}
