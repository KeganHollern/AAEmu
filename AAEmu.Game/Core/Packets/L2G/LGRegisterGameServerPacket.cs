using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Services.Security;

namespace AAEmu.Game.Core.Packets.L2G;

public class LGRegisterGameServerPacket() : LoginPacket(LGOffsets.LGRegisterGameServerPacket)
{
    public override void Read(PacketStream stream)
    {
        var result = stream.ReadByte();
        if (result != 0)
        {
            Logger.Error("Error registering on LoginServer");
            Connection.Close(SecurityDisconnectReason.AuthenticationRejected);
        }
        else
        {
            Connection.Register();
            Logger.Info("Successfully registered on LoginServer");
            _ = ModerationManager.Instance.RefreshLiveAccountsAsync();
        }
    }
}
