using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSAuctionMyBidListPacket() : GamePacket(CSOffsets.CSAuctionMyBidListPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var auctioneerId = stream.ReadBc();
        var auctioneerId2 = stream.ReadBc();
        var page = stream.ReadInt32();

        Logger.Warn($"AuctionMyBidList, auctioneerId: {auctioneerId}, auctioneerId2: {auctioneerId2}, Page: {page}");

        if (!ServiceInteraction.CanUseAuction(Connection.ActiveChar))
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.AucAuctioneerTooFarAway);
            return;
        }
        if (!LevelRestrictionConfig.Check(Connection.ActiveChar, AppConfiguration.Instance.LevelRestrictions.AuctionSearch))
            return;

        AuctionManager.Instance.GetBidAuctionLots(Connection.ActiveChar, page);
    }
}
