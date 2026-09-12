using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Auction.Templates;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSAuctionSearchPacket() : GamePacket(CSOffsets.CSAuctionSearchPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var auctioneerId = stream.ReadBc();
        var auctioneerId2 = stream.ReadBc();

        var auctionSearch = new AuctionSearch();
        stream.Read(auctionSearch);

        Logger.Warn($"AuctionSearch, auctioneerId: {auctioneerId}, auctioneerId: {auctioneerId2}, Keyword: {auctionSearch.Keyword}");

        if (!ServiceInteraction.CanUseAuction(Connection.ActiveChar))
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.AucAuctioneerTooFarAway);
            return;
        }
        if (!LevelRestrictionConfig.Check(Connection.ActiveChar, AppConfiguration.Instance.LevelRestrictions.AuctionSearch))
            return;

        AuctionManager.Instance.SearchAuctionLots(Connection.ActiveChar, auctionSearch);
    }
}
