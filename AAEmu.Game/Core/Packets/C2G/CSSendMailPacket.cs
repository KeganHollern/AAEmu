using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSendMailPacket() : GamePacket(CSOffsets.CSSendMailPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        Logger.Debug($"SendMail by {Connection.ActiveChar.Name}");

        var type = (MailType)stream.ReadByte();
        var receiverCharName = stream.ReadString();
        var unkId = stream.ReadUInt32(); //could be status
        var title = stream.ReadString();
        var text = stream.ReadString(); // TODO max length 1600
        var attachments = stream.ReadByte();
        var money0 = stream.ReadInt32();
        var money1 = stream.ReadInt32();
        var money2 = stream.ReadInt32();
        var extra = stream.ReadInt64();
        var itemSlots = new List<(SlotType slotType, byte slot)>();
        for (var i = 0; i < 10; i++)
        {
            var slotType = stream.ReadByte();
            var slot = stream.ReadByte();
            if (slotType == 0)
                itemSlots.Add((0, 0));
            else
                itemSlots.Add(((SlotType)slotType, slot));
        }

        var doodadObjId = stream.ReadBc();
        var mailCheckOK = ServiceInteraction.CanUseMailbox(Connection.ActiveChar, doodadObjId);

        if (mailCheckOK)
        {
            var mailResult = Connection.ActiveChar.Mails.SendMailToPlayer(type, receiverCharName, title, text, attachments, money0, money1, money2, extra, itemSlots);
            if (mailResult == MailResult.Success)
            {
                Connection.ActiveChar.SendErrorMessage(ErrorMessageType.MailSuccess);
            }
            else
            {
                Connection.SendPacket(new SCMailFailedPacket(mailResult, itemSlots.ToArray(), false));
            }
        }
        else
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.MailFailMailboxNotFound);
    }
}
