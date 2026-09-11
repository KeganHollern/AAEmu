using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Models.Account;

namespace AAEmu.Game.Core.Packets.G2L;

public class GLModerationRequestPacket(ModerationRequest request) : LoginPacket(GLOffsets.GLModerationRequestPacket)
{
    public override PacketStream Write(PacketStream stream)
    {
        if (!request.IsValid())
            throw new InvalidDataException("Invalid moderation request.");
        stream.Write(request.RequestId);
        stream.Write(request.ActorAccountId);
        stream.Write(request.ActorCharacterId);
        stream.Write(request.TargetAccountId);
        stream.Write((byte)request.Action);
        stream.Write(request.DurationSeconds);
        stream.Write(request.Reason);
        return stream;
    }
}
