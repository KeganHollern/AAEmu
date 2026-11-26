using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Mails;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(MailType.Normal)]
    [InlineData(MailType.Express)]
    public void Retention_NewPlayerMailStartsFromActualDelivery_AndSurvivesReload(MailType type)
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(type, 0));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        var expected = mail.Body.RecvDate.AddDays(30);
        Assert.False(MailManager.IsExpired(mail, expected.AddTicks(-1)));
        Assert.True(MailManager.IsExpired(mail, expected));

        var restored = graph.ReloadLifecycle().Mails.GetMailById(mail.Id);
        var deadline = Assert.IsType<DateTime>(restored.ExpiresAt);
        // MySQL DATETIME(6) stores microseconds, while Game uses 100 ns ticks.
        Assert.InRange(Math.Abs((deadline - expected).Ticks), 0, 9);
        Assert.False(MailManager.IsExpired(restored, deadline.AddTicks(-1)));
        Assert.True(MailManager.IsExpired(restored, deadline));
    }

    [Theory]
    [InlineData(MailType.SysExpress, "GM", 90)]
    [InlineData(MailType.Admin, "Private Test Staff", 90)]
    [InlineData(MailType.Normal, "Private Test Staff", 90)]
    [InlineData(MailType.Express, "Private Test Staff", 90)]
    [InlineData(MailType.Registered, "Private Test Staff", 14)]
    [InlineData(MailType.Spam, "Private Test Staff", 14)]
    [InlineData(MailType.Charged, ".inGameShop", 14)]
    [InlineData(MailType.Billing, ".houseTax", 14)]
    [InlineData(MailType.AucBidWin, ".Auctioneer", 14)]
    [InlineData(MailType.Demolish, ".housing", 14)]
    public void Retention_SystemSendPersistsOnlyTheSelectedCorrespondence(MailType type, string name, int days)
    {
        using var graph = new SendGraph();
        var received = new DateTime(2026, 11, 26, 10, 20, 30, DateTimeKind.Utc);
        var mail = new BaseMail
        {
            MailType = type, ReceiverName = graph.Receiver.Name, Title = "Private test",
            Header = { SenderId = 0, SenderName = name, ReceiverId = graph.Receiver.Id },
            Body = { Text = "Private test", SendDate = received.AddHours(-2), RecvDate = received }
        };
        Assert.True(mail.Send());
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));
        var restored = graph.ReloadLifecycle().Mails.GetMailById(mail.Id);
        Assert.Equal(days == 90 ? received.AddDays(90) : null, restored.ExpiresAt);
        var deadline = received.AddDays(days);
        Assert.False(MailManager.IsExpired(restored, deadline.AddTicks(-1)));
        Assert.True(MailManager.IsExpired(restored, deadline));
    }

    [Fact]
    public void Retention_AchievementRewardInFullBagCommitsOneNinetyDayLetter()
    {
        var itemIds = new Mock<IItemIdManager>();
        using var graph = new SendGraph(itemIds: itemIds.Object);
        var receiver = graph.Receiver;
        var templateId = graph.AddItem(0).TemplateId;
        itemIds.Setup(ids => ids.GetNextId()).Returns(receiver.Id + 80);
        receiver.Inventory.Bag.ContainerSize = 0;
        receiver.Inventory.Bag.UpdateFreeSlotCount();
        const uint achievementId = 6;
        Execute($"INSERT INTO character_achievements (character_id,achievement_id,completed_at,reward_status) " +
            $"VALUES ({receiver.Id},{achievementId},UTC_TIMESTAMP(),0)");
        Assert.Equal(AchievementRewardStatus.Mail, AchievementRewardManager.TryDeliver(receiver, achievementId, templateId));
        Assert.Equal(AchievementRewardStatus.Mail, AchievementRewardManager.TryDeliver(receiver, achievementId, templateId));
        var reward = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.Equal(".achievement", reward.Header.SenderName);
        Assert.Equal(reward.Body.RecvDate.AddDays(90), reward.ExpiresAt);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE receiver_id={receiver.Id}"));
        var restored = graph.ReloadLifecycle().Mails.GetMailById(reward.Id);
        var deadline = Assert.IsType<DateTime>(restored.ExpiresAt);
        Assert.InRange(Math.Abs((deadline - reward.ExpiresAt.Value).Ticks), 0, 9);
        Assert.Equal(templateId, Assert.Single(restored.Body.Attachments).TemplateId);
    }

    [Fact]
    public void Retention_EmptyFirstReadSetsFiveDays_AndReopenKeepsThatDeadline()
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 0));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        var deliveryDeadline = mail.ExpiresAt;
        graph.Sender.Mails.ReadMail(true, mail.Id);
        Assert.Equal(deliveryDeadline, mail.ExpiresAt);
        Assert.Equal(MailStatus.Unread, mail.Header.Status);
        graph.Receiver.Mails.ReadMail(false, mail.Id);
        Assert.Equal(MailStatus.Read, mail.Header.Status);
        var expiration = mail.OpenDate.AddDays(5);
        Assert.Equal(expiration, mail.ExpiresAt);
        graph.Receiver.Mails.ReadMail(false, mail.Id);
        Assert.Equal(expiration, mail.ExpiresAt);
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));
        var restored = graph.ReloadLifecycle().Mails.GetMailById(mail.Id);
        var persisted = Assert.IsType<DateTime>(restored.ExpiresAt);
        Assert.InRange(Math.Abs((persisted - expiration).Ticks), 0, 9);
        Assert.True(MailManager.IsExpired(restored, persisted));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retention_AttachedItemsAndCashKeepDeliveryDeadline_UntilTheFinalClaim(bool takeCashFirst)
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 99, 0));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        var deliveryDeadline = mail.Body.RecvDate.AddDays(30);
        var inbox = graph.Receiver.Mails;
        if (takeCashFirst)
            inbox.ReadMail(false, mail.Id);
        Assert.Equal(deliveryDeadline, mail.ExpiresAt);
        Assert.True(inbox.GetAttached(mail.Id, takeCashFirst, !takeCashFirst, false, item.Id));
        Assert.Equal(MailStatus.Read, mail.Header.Status);
        Assert.Equal(1, mail.GetTotalAttachmentCount());
        Assert.Equal(deliveryDeadline, mail.ExpiresAt);
        var before = DateTime.UtcNow;
        Assert.True(inbox.GetAttached(mail.Id, !takeCashFirst, takeCashFirst, false, item.Id));
        var final = Assert.IsType<DateTime>(mail.ExpiresAt);
        Assert.InRange(final, before.AddDays(5).AddTicks(-9), DateTime.UtcNow.AddDays(5).AddTicks(9));
        Assert.Equal(0, mail.GetTotalAttachmentCount());
        inbox.ReadMail(false, mail.Id);
        Assert.True(inbox.GetAttached(mail.Id, true, true, true));
        Assert.Equal(final, mail.ExpiresAt);
        Assert.True(graph.Save.TryCommitEconomy([graph.Receiver]));
        var saved = Assert.IsType<DateTime>(graph.ReloadLifecycle().Mails.GetMailById(mail.Id).ExpiresAt);
        Assert.InRange(Math.Abs((saved - final).Ticks), 0, 9);
        Assert.Equal(item.Id, Assert.Single(graph.Receiver.Inventory.Bag.Items).Id);
    }

    [Fact]
    public async Task Retention_MigrationAndReadLeaveExistingPlayerAndReturnedMailOnTheirOriginalDeadline()
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 17));
        var sent = Assert.Single(graph.Mails._allPlayerMails.Values);
        var delivered = DateTime.UtcNow.Date.AddDays(-1);
        // The old schema had no expiration value. Reload this row through the ordinary reader.
        Execute($"UPDATE mails SET expires_at=NULL, received_date='{delivered:yyyy-MM-dd}' WHERE id={sent.Id}");
        var update = Path.Combine(AppContext.BaseDirectory, "SQL", "updates", "2026-11-26_aaemu_game_mail_retention.sql");
        var sql = await File.ReadAllTextAsync(update, TestContext.Current.CancellationToken);
        Execute(sql);
        Execute(sql);
        var (items, mails) = graph.ReloadLifecycle(useReloadedCheckpoint: true);
        SwapSingleton(items);
        SwapSingleton(mails);
        var legacy = mails.GetMailById(sent.Id);
        Assert.Null(legacy.ExpiresAt);
        graph.Receiver.Mails.ReadMail(false, legacy.Id);
        Assert.Null(legacy.ExpiresAt);
        var deadline = delivered + MailManager.MailExpireDelay;
        mails.CheckAllMailTimings(deadline.AddTicks(-1));
        Assert.Same(legacy, mails.GetMailById(legacy.Id));
        mails.CheckAllMailTimings(deadline);
        var returned = Assert.Single(mails._allPlayerMails.Values.Where(letter => letter.Header.Returned &&
            letter.Header.ReceiverId == graph.Sender.Id));
        Assert.Null(returned.ExpiresAt);
        Assert.Equal(17, returned.Body.CopperCoins);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={returned.Id} AND expires_at IS NULL"));
        Assert.True(MailManager.IsExpired(returned, deadline + MailManager.MailExpireDelay));
    }

    [Fact]
    public void Retention_ReturnAtDeadlineRetriesOnce_AndReturnedLetterUsesNewThirtyDays()
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 29, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        var deadline = source.Body.RecvDate.AddDays(30);
        var commit = graph.Mails.CommitLifecycle;
        graph.Mails.CommitLifecycle = _ => false;
        graph.Mails.CheckAllMailTimings(deadline);
        Assert.Same(source, Assert.Single(graph.Mails._allPlayerMails.Values));
        Assert.Equal(deadline, source.ExpiresAt);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        graph.Mails.CommitLifecycle = commit;
        graph.Mails.CheckAllMailTimings(deadline);
        graph.Mails.CheckAllMailTimings(deadline);
        var returned = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.True(returned.Header.Returned);
        Assert.Equal(deadline.AddDays(30), returned.ExpiresAt);
        Assert.Equal(29, returned.Body.CopperCoins);
        Assert.Same(item, Assert.Single(returned.Body.Attachments));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        var restored = graph.ReloadLifecycle().Mails.GetMailById(returned.Id);
        var expires = Assert.IsType<DateTime>(restored.ExpiresAt);
        Assert.False(MailManager.IsExpired(restored, expires.AddTicks(-1)));
        Assert.True(MailManager.IsExpired(restored, expires));
    }
}
