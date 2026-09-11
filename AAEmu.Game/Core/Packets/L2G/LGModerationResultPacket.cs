using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Models.Account;

namespace AAEmu.Game.Core.Packets.L2G;

public class LGModerationResultPacket() : LoginPacket(LGOffsets.LGModerationResultPacket)
{
    internal static ModerationResult ReadBody(PacketStream stream)
    {
        if (stream.LeftBytes != 39)
            throw new InvalidDataException("Moderation result must contain exactly 39 bytes.");
        var requestId = stream.ReadUInt64();
        var status = (ModerationStatus)stream.ReadByte();
        var state = ModerationState.Read(stream);
        if (requestId == 0 || !Enum.IsDefined(status) || stream.Pos != stream.Count)
            throw new InvalidDataException("Invalid moderation result.");
        return new ModerationResult(requestId, status, state);
    }

    public override void Read(PacketStream stream)
        => ModerationManager.Instance.CompleteRequest(ReadBody(stream));
}
