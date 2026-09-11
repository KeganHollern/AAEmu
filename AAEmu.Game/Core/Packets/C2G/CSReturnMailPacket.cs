using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSReturnMailPacket() : GamePacket(CSOffsets.CSReturnMailPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.Count - stream.Pos != sizeof(long))
            return;
        var mailId = stream.ReadInt64();
        if (mailId > 0)
            Connection.ActiveChar?.Mails.ReturnMail(mailId);
    }
}
