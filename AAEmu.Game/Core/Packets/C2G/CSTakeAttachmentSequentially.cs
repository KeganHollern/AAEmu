using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSTakeAttachmentSequentially() : GamePacket(CSOffsets.CSTakeAttachmentSequentially, 1)
{
    public override void Read(PacketStream stream)
    {
        var mailId = stream.ReadInt64();
        if (!ServiceInteraction.CanUseMailbox(Connection.ActiveChar))
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.MailFailMailboxNotFound);
            return;
        }
        Logger.Debug("TakeAttachmentSequentially, mailId: {0}", mailId);
        var mail = MailManager.Instance.GetMailById(mailId);
        if (mail == null || mail.Header.ReceiverId != Connection.ActiveChar.Id) // just a check for hackers trying to steal mails
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.MailInvalid);
        }
        else
        {
            Connection.ActiveChar.Mails.GetAttached(mailId, true, true, true);
        }
    }
}
