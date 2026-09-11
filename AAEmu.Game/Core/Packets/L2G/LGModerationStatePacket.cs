using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Models.Account;

namespace AAEmu.Game.Core.Packets.L2G;

public class LGModerationStatePacket() : LoginPacket(LGOffsets.LGModerationStatePacket)
{
    internal static ModerationState ReadBody(PacketStream stream)
    {
        if (stream.LeftBytes != 30)
            throw new InvalidDataException("Moderation state must contain exactly 30 bytes.");
        var state = ModerationState.Read(stream);
        if (stream.Pos != stream.Count)
            throw new InvalidDataException("Unexpected moderation state data.");
        return state;
    }

    public override void Read(PacketStream stream)
        => ModerationManager.Instance.ApplyState(ReadBody(stream));
}
