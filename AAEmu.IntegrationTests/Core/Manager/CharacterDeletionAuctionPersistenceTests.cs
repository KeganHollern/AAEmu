using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Mails;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class AuctionSettlementPersistenceTests
{
    [Theory]
    [InlineData("seller", true)]
    [InlineData("seller", false)]
    [InlineData("bidder", true)]
    public void CharacterDeletion_WaitsForNormalAuctionSettlementAndPreservesOtherPlayers(string deleting, bool hasBid)
    {
        using var graph = new AuctionGraph();
        var item = graph.AddItem();
        graph.Post(item);
        var lot = Assert.Single(graph.Auction.AuctionLots.Values);
        if (hasBid)
            graph.Bid(graph.Buyer, lot.Id, 500);
        var player = deleting == "seller" ? graph.Seller : graph.Buyer;
        player.DeleteRequestTime = DateTime.UtcNow.AddMinutes(-2);
        player.DeleteTime = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var oldAccount = AppConfiguration.Instance.Account;
        AppConfiguration.Instance.Account = new AccountConfig();
        var saveField = typeof(Singleton<SaveManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldSave = saveField.GetValue(null);
        saveField.SetValue(null, graph.Save);
        try
        {
            var manager = AuctionDeletionManager(graph);
            using var database = MySQL.CreateConnection();
            var deadline = lot.EndTime;
            Assert.False(manager.CheckForDeletedCharactersDeletion(player, null, database));
            Assert.False(player.IsDeleted);
            Assert.Equal(deadline, lot.EndTime);
            Assert.Empty(graph.OwnMails());
            Assert.Equal(hasBid ? 500 : 0, lot.BidMoney);
            lot.EndTime = DateTime.UtcNow.AddSeconds(-1);
            var trigger = $"deletion_auction_fail_{graph.Id}";
            Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON mails FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected auction mail failure'");
            try
            {
                graph.Auction.UpdateAuctionHouse();
                Assert.False(manager.CheckForDeletedCharactersDeletion(player, null, database));
                Assert.Contains(lot.Id, graph.Auction.AuctionLots.Keys);
                Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
            }
            finally { Execute($"DROP TRIGGER {trigger}"); }
            graph.Auction.UpdateAuctionHouse();
            Assert.DoesNotContain(lot.Id, graph.Auction.AuctionLots.Keys);
            Assert.True(manager.CheckForDeletedCharactersDeletion(player, null, database));
            Assert.True(graph.Save.TryCommitEconomy([]));
            Assert.Equal(1, Read("characters", "deleted", player.Id));
            Assert.Equal(0, Read("characters", "money", player.Id));
            Assert.DoesNotContain(graph.OwnMails(), mail => mail.Header.ReceiverId == player.Id);
            if (deleting == "seller" && hasBid)
            {
                var delivery = Assert.Single(graph.OwnMails());
                Assert.Equal(MailType.AucBidWin, delivery.MailType);
                Assert.Equal(graph.Buyer.Id, delivery.Header.ReceiverId);
                Assert.Same(item, Assert.Single(delivery.Body.Attachments));
                Assert.Equal(9500, Read("characters", "money", graph.Buyer.Id));
            }
            else if (deleting == "bidder")
            {
                var proceeds = Assert.Single(graph.OwnMails());
                Assert.Equal(MailType.AucOffSuccess, proceeds.MailType);
                Assert.Equal(graph.Seller.Id, proceeds.Header.ReceiverId);
                Assert.Equal(450, proceeds.Body.CopperCoins);
                Assert.Null(graph.Items.GetItemByItemId(item.Id));
            }
            else
            {
                Assert.Empty(graph.OwnMails());
                Assert.Null(graph.Items.GetItemByItemId(item.Id));
            }
        }
        finally
        {
            saveField.SetValue(null, oldSave);
            AppConfiguration.Instance.Account = oldAccount;
        }
    }

    [Fact]
    public void CharacterDeletion_CancellationDuringAuctionWaitPreservesOrdinarySettlement()
    {
        using var graph = new AuctionGraph();
        var item = graph.AddItem();
        graph.Post(item);
        var lot = Assert.Single(graph.Auction.AuctionLots.Values);
        graph.Bid(graph.Buyer, lot.Id, 500);
        graph.Seller.DeleteRequestTime = DateTime.UtcNow.AddMinutes(-2);
        graph.Seller.DeleteTime = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(graph.Save.TryCommitEconomy([graph.Seller]));
        var manager = AuctionDeletionManager(graph);
        using var database = MySQL.CreateConnection();
        Assert.False(manager.CheckForDeletedCharactersDeletion(graph.Seller, null, database));
        var connection = new GameConnection(Mock.Of<ISession>());
        Assert.True(connection.TryAuthenticate(graph.Seller.AccountId));
        connection.Characters.Add(graph.Seller.Id, graph.Seller);
        manager.SetRestoreCharacter(connection, graph.Seller.Id);
        Assert.Equal(DateTime.MinValue, graph.Seller.DeleteTime);
        lot.EndTime = DateTime.UtcNow.AddSeconds(-1);
        graph.Auction.UpdateAuctionHouse();
        Assert.False(manager.CheckForDeletedCharactersDeletion(graph.Seller, null, database));
        Assert.Equal(0, Read("characters", "deleted", graph.Seller.Id));
        Assert.Equal(2, graph.OwnMails().Length);
        Assert.Equal(450, graph.OwnMails().Single(mail => mail.MailType == MailType.AucOffSuccess).Body.CopperCoins);
    }

    private static CharacterManager AuctionDeletionManager(AuctionGraph graph) => new(
        Mock.Of<IWorldManager>(), Mock.Of<IAccountManager>(), Mock.Of<INameManager>(), Mock.Of<ICharacterIdManager>(),
        Mock.Of<IFactionManager>(), Mock.Of<ISkillManager>(), graph.Items, Mock.Of<IHousingManager>(),
        Mock.Of<IFamilyManager>(), graph.Mail, Mock.Of<ITaskManager>());
}
