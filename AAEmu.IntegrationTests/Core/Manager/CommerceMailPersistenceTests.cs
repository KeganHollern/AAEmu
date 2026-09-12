using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Units;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Fact]
    public void OfflineRecipientBlock_RejectsMailUntilPersistedUnblock()
    {
        using var graph = new SendGraph();
        var item = graph.AddEquipment(0);
        Execute($"INSERT INTO blocked(owner,blocked_id) VALUES({graph.Receiver.Id},{graph.Sender.Id})");
        Assert.Equal(MailResult.CanNotBeMailed, graph.Send(MailType.Express, 100, 0));
        Assert.Equal(10000, graph.Sender.Money);
        Assert.Same(item, Assert.Single(graph.Sender.Inventory.Bag.Items));
        Assert.Empty(graph.Mails._allPlayerMails);

        graph.Receiver.Blocked = new CharacterBlocked(graph.Receiver);
        using (var connection = MySQL.CreateConnection())
            graph.Receiver.Blocked.Load(connection);
        graph.Receiver.Blocked.RemoveBlockedUser(graph.Sender.Name);
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM blocked WHERE owner={graph.Receiver.Id}"));
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 100, 0));
    }

    [Fact]
    public void UnblockAfterSaveSnapshot_RemainsPendingAfterTheEarlierCommit()
    {
        using var graph = new SendGraph();
        var blocked = graph.Receiver.Blocked = new CharacterBlocked(graph.Receiver);
        blocked.AddBlockedUser(graph.Sender.Name);
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));
        blocked.RemoveBlockedUser(graph.Sender.Name);
        blocked.AddBlockedUser(graph.Sender.Name);
        using (var connection = MySQL.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            var context = new PersistenceSaveContext(connection, transaction);
            blocked.Save(context);
            blocked.RemoveBlockedUser(graph.Sender.Name);
            transaction.Commit();
            context.AcknowledgeCommit();
        }
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));
        var reloaded = new CharacterBlocked(graph.Receiver);
        using (var connection = MySQL.CreateConnection())
            reloaded.Load(connection);
        Assert.False(reloaded.Contains(graph.Sender.Id));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM blocked WHERE owner={graph.Receiver.Id}"));
    }

    [Fact]
    public void DeletedRecipient_RejectsMailDespiteOldNameCache()
    {
        using var graph = new SendGraph();
        Execute($"UPDATE characters SET deleted=1 WHERE id={graph.Receiver.Id}");
        Assert.Equal(graph.Receiver.Id, NameManager.Instance.GetCharacterId(graph.Receiver.Name));
        Assert.Equal(MailResult.UnableToFindRecipient, graph.Send(MailType.Express, 100));
        Assert.Equal(10000, graph.Sender.Money);
        Assert.Empty(graph.Mails._allPlayerMails);
    }

    [Fact]
    public void CharacterDeletion_CancelledInDatabaseDoesNotReturnMailFromStaleCharacter()
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 77));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        graph.Receiver.DeleteTime = DateTime.UtcNow.AddMinutes(-1);
        using var connection = MySQL.CreateConnection();
        Assert.False(DeletionManager(graph.Mails).CheckForDeletedCharactersDeletion(graph.Receiver, null, connection));
        Assert.Same(source, Assert.Single(graph.Mails._allPlayerMails.Values));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Equal(77, Scalar($"SELECT money_amount_1 FROM mails WHERE id={source.Id}"));
        Assert.Equal(0, Scalar($"SELECT deleted FROM characters WHERE id={graph.Receiver.Id}"));
    }

    [Fact]
    public void CharacterDeletion_PartialMailFailureStaysPendingThenReloadCompletesOnlyItsReceiver()
    {
        using var graph = new SendGraph();
        var firstItem = graph.AddEquipment(0);
        var secondItem = graph.AddItem(1);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Normal, 77, 0));
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 88, 1));
        var sources = graph.Mails._allPlayerMails.Values.OrderBy(mail => mail.Id).ToArray();
        sources[0].Body.BillingAmount = 13;
        sources[0].Body.MoneyAmount2 = 29;
        sources[0].IsDirty = true;

        var other = new Character(new UnitCustomModelParams())
        {
            Id = graph.Sender.Id + 2, AccountId = graph.Sender.Id + 2, Name = $"Other{graph.Sender.Id}",
            Faction = new SystemFaction(), FactionName = "", Slots = [], Created = DateTime.UtcNow
        };
        NameManager.Instance.AddCharacter(other.Id, other.Name, other.AccountId);
        Assert.True(graph.Save.TryCommitEconomy([other]));
        var unrelatedA = AddUnrelatedMail(graph.Mails, graph.Sender, other, 31);
        var unrelatedB = AddUnrelatedMail(graph.Mails, other, graph.Sender, 47);
        graph.Receiver.DeleteTime = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));

        var oldAccount = AppConfiguration.Instance.Account;
        AppConfiguration.Instance.Account = new AccountConfig { DeleteReleaseName = false };
        try
        {
            var commit = graph.Mails.CommitLifecycle;
            var calls = 0;
            graph.Mails.CommitLifecycle = write => ++calls == 2 ? false : commit(write);
            var manager = DeletionManager(graph.Mails);
            using var connection = MySQL.CreateConnection();
            Assert.False(manager.CheckForDeletedCharactersDeletion(graph.Receiver, null, connection));
            Assert.Equal(0, Scalar($"SELECT deleted FROM characters WHERE id={graph.Receiver.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE receiver_id={graph.Receiver.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={sources[0].Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={sources[1].Id}"));

            var restored = graph.ReloadLifecycle(useReloadedCheckpoint: true);
            var restartedManager = DeletionManager(restored.Mails);
            Assert.True(restartedManager.CheckForDeletedCharactersDeletion(graph.Receiver, null, connection));
            Assert.False(restartedManager.CheckForDeletedCharactersDeletion(graph.Receiver, null, connection));
            Assert.Equal(1, Scalar($"SELECT deleted FROM characters WHERE id={graph.Receiver.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE receiver_id={graph.Receiver.Id}"));
            Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id IN({sources[0].Id},{sources[1].Id})"));
            Assert.Equal(31, Scalar($"SELECT money_amount_1 FROM mails WHERE id={unrelatedA.Id}"));
            Assert.Equal(47, Scalar($"SELECT money_amount_1 FROM mails WHERE id={unrelatedB.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id IN({unrelatedA.Id},{unrelatedB.Id})"));
            var returned = restored.Mails._allPlayerMails.Values.Where(mail => mail.Header.Returned &&
                mail.Header.SenderId == graph.Receiver.Id).OrderBy(mail => mail.Body.CopperCoins).ToArray();
            Assert.Equal(new[] { 77, 88 }, returned.Select(mail => mail.Body.CopperCoins));
            Assert.Equal(13, returned[0].Body.BillingAmount);
            Assert.Equal(29, returned[0].Body.MoneyAmount2);
            Assert.Equal(new[] { firstItem.Id, secondItem.Id }, returned.SelectMany(mail => mail.Body.Attachments).Select(item => item.Id));
            Assert.All(returned.SelectMany(mail => mail.Body.Attachments), item => Assert.Equal(graph.Sender.Id, item.OwnerId));
            Assert.Equal(MailResult.UnableToFindRecipient, graph.Send(MailType.Express, 100));
        }
        finally
        {
            AppConfiguration.Instance.Account = oldAccount;
        }
    }

    private static BaseMail AddUnrelatedMail(MailManager mails, Character receiver, Character sender, int copper)
    {
        var mail = new BaseMail
        {
            MailType = MailType.Normal, ReceiverName = receiver.Name, Title = "Unrelated",
            Header = { ReceiverId = receiver.Id, SenderId = sender.Id, SenderName = sender.Name },
            Body = { CopperCoins = copper, Text = "Keep", SendDate = DateTime.UtcNow, RecvDate = DateTime.UtcNow.AddHours(1) }
        };
        Assert.True(mails.Send(mail));
        return mail;
    }

    private static CharacterManager DeletionManager(IMailManager mails) => new(
        Mock.Of<IWorldManager>(), Mock.Of<IAccountManager>(), NameManager.Instance, Mock.Of<ICharacterIdManager>(),
        Mock.Of<IFactionManager>(), Mock.Of<ISkillManager>(), Mock.Of<IItemManager>(), Mock.Of<IHousingManager>(),
        Mock.Of<IFamilyManager>(), mails, Mock.Of<ITaskManager>());
}
