using System.Buffers.Binary;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Fact]
    public void CharacterDeletion_ActiveCharacterRejectsRequestAndTimerWithoutChangingItsRow()
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        var connection = DeletionConnection(player);
        connection.ActiveChar = player;
        var manager = AssetDeletionManager(graph);
        manager.SetDeleteCharacter(connection, player.Id);
        Assert.Equal(DateTime.MinValue, player.DeleteTime);
        MarkDeletionDue(player);
        using var database = MySQL.CreateConnection();
        Assert.False(manager.CheckForDeletedCharactersDeletion(player, connection, database));
        Assert.Equal(0, Scalar($"SELECT deleted FROM characters WHERE id={player.Id}"));
        Assert.Equal(10000, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
    }

    [Theory]
    [InlineData((uint)BuffConstants.Prisoner_Nuian, 60000, false, ErrorMessageType.CannotDeleteCharWhilePenalty)]
    [InlineData((uint)BuffConstants.Prisoner_Haranyan, 60000, false, ErrorMessageType.CannotDeleteCharWhilePenalty)]
    [InlineData((uint)BuffConstants.ForciblyAwaitingTrial, 60000, false, ErrorMessageType.CannotDeleteCharWhilePenalty)]
    [InlineData((uint)BuffConstants.SuspectedUser, 0, false, ErrorMessageType.CannotDeleteCharWhileBotSuspected)]
    [InlineData((uint)BuffConstants.PrimeSuspect, 60000, false, ErrorMessageType.CannotDeleteCharWhileBotSuspected)]
    [InlineData((uint)BuffConstants.TransformingIntoPrimeSuspect, 60000, false, ErrorMessageType.CannotDeleteCharWhileBotSuspected)]
    [InlineData((uint)BuffConstants.Prisoner_Bot, 60000, false, ErrorMessageType.CannotDeleteCharWhileBotSuspected)]
    public void CharacterDeletion_OfflineSavedPenaltyRejectsRequest(uint buff, int duration, bool realTime, ErrorMessageType expected)
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        Execute($"INSERT INTO character_active_buffs(character_id,buff_id,caster_id,duration,time_left,charge,real_time) VALUES({player.Id},{buff},{player.Id},{duration},{duration},0,{(realTime ? 1 : 0)})");
        var packets = new List<byte[]>();
        var connection = DeletionConnection(player, packets);
        AssetDeletionManager(graph).SetDeleteCharacter(connection, player.Id);
        Assert.Equal(DateTime.MinValue, player.DeleteTime);
        var error = Assert.Single(packets);
        Assert.Equal(SCOffsets.SCErrorMsgPacket, BinaryPrimitives.ReadUInt16LittleEndian(error.AsSpan(6)));
        Assert.Equal((ushort)expected, BinaryPrimitives.ReadUInt16LittleEndian(error.AsSpan(8)));
    }

    [Fact]
    public void CharacterDeletion_PendingTrialOnDurableRowRejectsStaleLobbyObject()
    {
        using var graph = new SendGraph();
        Execute($"UPDATE characters SET offline_guilty_time=-1 WHERE id={graph.Sender.Id}");
        Assert.Equal(0, graph.Sender.OfflineGuiltyTime);
        AssetDeletionManager(graph).SetDeleteCharacter(DeletionConnection(graph.Sender), graph.Sender.Id);
        Assert.Equal(DateTime.MinValue, graph.Sender.DeleteTime);
        Assert.Equal(-1, graph.Sender.OfflineGuiltyTime);
    }

    [Fact]
    public void CharacterDeletion_RemovesPersonalAssetsPreservesOtherPlayersAndRejectsStaleSave()
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        var item = graph.AddItem(0);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var mateId = player.Id + 10;
        Execute($"INSERT INTO mates(id,item_id,name,xp,level,mileage,hp,mp,owner) VALUES({mateId},{item.Id},'Pet',0,1,0,1,1,{player.Id})");
        Execute($"INSERT INTO slaves(id,item_id,summoner) VALUES({mateId},{item.Id},{player.Id})");
        Execute($"INSERT INTO accounts(account_id,credits,loyalty,labor) VALUES({player.AccountId},7,8,9)");
        var systemMail = AddSystemMail(graph.Mails, player, 71);
        var otherMail = AddSystemMail(graph.Mails, graph.Receiver, 73);
        var stale = new Character(new UnitCustomModelParams()) { Id = player.Id, AccountId = player.AccountId, Name = player.Name };
        var manager = AssetDeletionManager(graph);
        MarkDeletionDue(player);
        var oldAccount = AppConfiguration.Instance.Account;
        AppConfiguration.Instance.Account = new AccountConfig { DeleteReleaseName = true };
        try
        {
            using var database = MySQL.CreateConnection();
            Assert.True(manager.CheckForDeletedCharactersDeletion(player, null, database));
            Assert.True(player.IsDeleted);
            Assert.Equal(1, Scalar($"SELECT deleted FROM characters WHERE id={player.Id}"));
            Assert.Equal(0, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE owner={player.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM item_containers WHERE owner_id={player.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mates WHERE owner={player.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM slaves WHERE summoner={player.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={systemMail.Id}"));
            Assert.Equal(73, Scalar($"SELECT money_amount_1 FROM mails WHERE id={otherMail.Id}"));
            Assert.Equal(24, Scalar($"SELECT credits+loyalty+labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Null(graph.Items.GetItemByItemId(item.Id));
            Assert.Empty(player.Inventory.Bag.Items);
            Assert.True(graph.Save.TryCommitEconomy([stale]));
            Assert.True(stale.IsDeleted);
            Assert.Equal(1, Scalar($"SELECT deleted FROM characters WHERE id={player.Id} AND name='!{player.Name}'"));
            Assert.Equal(0u, NameManager.Instance.GetCharacterId(player.Name));
            Assert.False(manager.TrySelectCharacter(DeletionConnection(stale), stale));
            Assert.False(manager.CheckForDeletedCharactersDeletion(player, null, database));
            manager.SetRestoreCharacter(DeletionConnection(stale), stale.Id);
            Assert.Equal(1, Scalar($"SELECT deleted FROM characters WHERE id={player.Id}"));
        }
        finally { AppConfiguration.Instance.Account = oldAccount; }
    }

    [Fact]
    public void CharacterDeletion_AssetFailureRollsBackTombstoneAndMailAndRetainsInventoryForRetry()
    {
        using var settings = new DeletionSettings();
        using var graph = new SendGraph();
        var player = graph.Sender;
        var item = graph.AddItem(0);
        var mail = AddSystemMail(graph.Mails, player, 41);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        MarkDeletionDue(player);
        var trigger = $"delete_failure_{player.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE DELETE ON items FOR EACH ROW BEGIN IF OLD.id={item.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected deletion failure'; END IF; END");
        var manager = AssetDeletionManager(graph);
        using var database = MySQL.CreateConnection();
        try
        {
            Assert.False(manager.CheckForDeletedCharactersDeletion(player, null, database));
            Assert.False(player.IsDeleted);
            Assert.Equal(0, Scalar($"SELECT deleted FROM characters WHERE id={player.Id}"));
            Assert.Equal(10000, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={mail.Id}"));
            Assert.Same(mail, graph.Mails.GetMailById(mail.Id));
            Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
        }
        finally { Execute($"DROP TRIGGER {trigger}"); }
        Assert.True(manager.CheckForDeletedCharactersDeletion(player, null, database));
        Assert.True(graph.Save.TryCommitEconomy([player]));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
    }

    [Fact]
    public void CharacterDeletion_PendingOrDeletedDurableRowCannotEnterWorldThroughStaleSelection()
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        var manager = AssetDeletionManager(graph);
        Execute($"UPDATE characters SET delete_time=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY) WHERE id={player.Id}");
        Assert.Equal(DateTime.MinValue, player.DeleteTime);
        Assert.False(manager.TrySelectCharacter(DeletionConnection(player), player));
        Execute($"UPDATE characters SET delete_time='0001-01-01',deleted=1 WHERE id={player.Id}");
        Assert.False(manager.TrySelectCharacter(DeletionConnection(player), player));
    }

    [Fact]
    public void CharacterDeletion_PreservesSharedCofferContentsAndArchivedEvidence()
    {
        using var settings = new DeletionSettings();
        using var graph = new SendGraph();
        var player = graph.Sender;
        var cofferItem = graph.AddItem(0);
        var archiveItem = graph.AddItem(1);
        var personalItem = graph.AddItem(2);
        var coffer = new CofferContainer(player.Id, false) { ContainerId = player.Id * 20UL };
        var containers = (Dictionary<ulong, ItemContainer>)typeof(ItemManager)
            .GetField("_allPersistentContainers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(graph.Items)!;
        containers.Add(coffer.ContainerId, coffer);
        player.Inventory.Bag.Items.Remove(cofferItem);
        coffer.Items.Add(cofferItem);
        cofferItem._holdingContainer = coffer;
        cofferItem.SlotType = SlotType.Trade;
        Assert.True(graph.Save.TryCommitEconomy([player]));
        Execute($"INSERT INTO mail_archive_items(item_id,mail_id,item_row) VALUES({archiveItem.Id},{player.Id},JSON_OBJECT('evidence','retained'))");
        graph.Items.DetachArchivedMailItem(archiveItem);
        MarkDeletionDue(player);
        using var database = MySQL.CreateConnection();
        Assert.True(AssetDeletionManager(graph).CheckForDeletedCharactersDeletion(player, null, database));
        Assert.True(graph.Save.TryCommitEconomy([player]));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={cofferItem.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM item_containers WHERE container_id={coffer.ContainerId}"));
        Assert.Same(cofferItem, graph.Items.GetItemByItemId(cofferItem.Id));
        Assert.Same(cofferItem, Assert.Single(coffer.Items));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={archiveItem.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={archiveItem.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={personalItem.Id}"));
    }

    [Fact]
    public void CharacterDeletion_CancelledPendingRequestCanEnterWorldAndKeepsAssets()
    {
        using var settings = new DeletionSettings();
        using var graph = new SendGraph();
        var player = graph.Sender;
        var item = graph.AddItem(0);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        MarkDeletionDue(player);
        var connection = DeletionConnection(player);
        var manager = AssetDeletionManager(graph);
        manager.SetRestoreCharacter(connection, player.Id);
        Assert.Equal(DateTime.MinValue, player.DeleteTime);
        Assert.True(manager.TrySelectCharacter(connection, player));
        using var database = MySQL.CreateConnection();
        Assert.False(manager.CheckForDeletedCharactersDeletion(player, connection, database));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
    }

    [Fact]
    public void CharacterDeletion_AnotherSelectedSessionBlocksStaleTimerBeforeWorldRegistration()
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        var selected = new Character(new UnitCustomModelParams()) { Id = player.Id, AccountId = player.AccountId };
        var connection = DeletionConnection(selected);
        Assert.True(connection.TrySelectCharacter(selected));
        var oldTable = SwapSingleton(new GameConnectionTable());
        try
        {
            Assert.True(GameConnectionTable.Instance.AddConnection(connection));
            MarkDeletionDue(player);
            using var database = MySQL.CreateConnection();
            Assert.False(AssetDeletionManager(graph).CheckForDeletedCharactersDeletion(player, null, database));
            Assert.False(player.IsDeleted);
            Assert.Equal(0, Scalar($"SELECT deleted FROM characters WHERE id={player.Id}"));
        }
        finally { SwapSingleton(oldTable); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CharacterDeletion_ExpiredRealTimePenaltyAllowsCompletionButGameTimePenaltyRemains(bool realTime, bool expected)
    {
        using var settings = new DeletionSettings();
        using var graph = new SendGraph();
        var player = graph.Sender;
        MarkDeletionDue(player);
        Execute($"INSERT INTO character_active_buffs(character_id,buff_id,caster_id,duration,time_left,charge,real_time,saved_at) VALUES({player.Id},{(uint)BuffConstants.Prisoner_Nuian},{player.Id},60000,60000,0,{(realTime ? 1 : 0)},DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 HOUR))");
        using var database = MySQL.CreateConnection();
        Assert.Equal(expected, AssetDeletionManager(graph).CheckForDeletedCharactersDeletion(player, null, database));
        Assert.Equal(expected ? 1 : 0, Scalar($"SELECT deleted FROM characters WHERE id={player.Id}"));
    }

    private sealed class DeletionSettings : IDisposable
    {
        private readonly AccountConfig _old = AppConfiguration.Instance.Account;
        public DeletionSettings() => AppConfiguration.Instance.Account = new AccountConfig { DeleteReleaseName = true };
        public void Dispose() => AppConfiguration.Instance.Account = _old;
    }

    private static CharacterManager AssetDeletionManager(SendGraph graph) => new(
        Mock.Of<IWorldManager>(), Mock.Of<IAccountManager>(), NameManager.Instance, Mock.Of<ICharacterIdManager>(),
        Mock.Of<IFactionManager>(), Mock.Of<ISkillManager>(), graph.Items, Mock.Of<IHousingManager>(),
        Mock.Of<IFamilyManager>(), graph.Mails, Mock.Of<ITaskManager>());

    private static GameConnection DeletionConnection(Character player, List<byte[]> packets = null)
    {
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Ip).Returns(System.Net.IPAddress.Loopback);
        session.Setup(value => value.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(bytes => packets?.Add(bytes));
        var connection = new GameConnection(session.Object);
        Assert.True(connection.TryAuthenticate(player.AccountId));
        connection.Characters.Add(player.Id, player);
        return connection;
    }

    private static void MarkDeletionDue(Character player)
    {
        player.DeleteRequestTime = DateTime.UtcNow.AddMinutes(-2);
        player.DeleteTime = DateTime.UtcNow.AddMinutes(-1);
        Execute($"UPDATE characters SET delete_request_time=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 2 MINUTE),delete_time=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 MINUTE) WHERE id={player.Id}");
    }

    private static BaseMail AddSystemMail(MailManager manager, Character receiver, int money)
    {
        var mail = new BaseMail
        {
            MailType = MailType.SysExpress, ReceiverName = receiver.Name, Title = "System",
            Header = { ReceiverId = receiver.Id, SenderName = ".system" },
            Body = { Text = "System", CopperCoins = money, SendDate = DateTime.UtcNow, RecvDate = DateTime.UtcNow }
        };
        Assert.True(manager.Send(mail));
        return mail;
    }
}
