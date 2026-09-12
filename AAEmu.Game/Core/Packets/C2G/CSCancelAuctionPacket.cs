using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Auction;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCancelAuctionPacket() : GamePacket(CSOffsets.CSCancelAuctionPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var auctioneerId = stream.ReadBc();
        var auctioneerId2 = stream.ReadBc();

        var lot = new AuctionLot();
        stream.Read(lot);

        Logger.Warn($"AuctionCancel, auctioneerId: {auctioneerId}, auctioneerId2: {auctioneerId2}, ClientName: {lot.ClientName}, LotId: {lot.Id}");

        if (!ServiceInteraction.CanUseAuction(Connection.ActiveChar))
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.AucAuctioneerTooFarAway);
            return;
        }

        AuctionManager.Instance.CancelAuctionLot(Connection.ActiveChar, lot.Id);
    }
}
