using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSTakeAttachmentMoneyPacket() : GamePacket(CSOffsets.CSTakeAttachmentMoneyPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var mailId = stream.ReadInt64();
        if (!ServiceInteraction.CanUseMailbox(Connection.ActiveChar))
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.MailFailMailboxNotFound);
            return;
        }
        Connection.ActiveChar.Mails.GetAttached(mailId, true, false, true);
    }
}
