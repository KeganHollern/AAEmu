using System.Text.Json;
using System.Text.Json.Nodes;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Fact]
    public async Task MailLifecycle_MigrationIsIdempotentAndNonDestructive()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SQL", "updates", "2026-09-11_aaemu_game_mail_lifecycle.sql");
        var sql = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var mails = Scalar("SELECT COUNT(*) FROM mails");
        var items = Scalar("SELECT COUNT(*) FROM items");
        Execute(sql);
        Execute(sql);
        Assert.Equal(mails, Scalar("SELECT COUNT(*) FROM mails"));
        Assert.Equal(items, Scalar("SELECT COUNT(*) FROM items"));
    }

    [Theory]
    [InlineData(MailType.Normal, MailStatus.Unread)]
    [InlineData(MailType.Express, MailStatus.Unread)]
    [InlineData(MailType.Normal, MailStatus.Read)]
    [InlineData(MailType.Express, MailStatus.Read)]
    public void Expiry_DeadlineReturnsContentsOnceWithoutFees_ThenArchivesReturn(MailType type, MailStatus status)
    {
        using var graph = new SendGraph();
        var item = graph.AddEquipment(0);
        Assert.Equal(MailResult.Success, graph.Send(type, 123, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        source.Body.RecvDate = now - MailManager.MailExpireDelay;
        source.Header.Status = status;
        source.OpenDate = now.AddDays(-1); // Reading never extends the delivery-based deadline.
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        var senderMoney = graph.Sender.Money;
        var receiverMoney = graph.Receiver.Money;

        graph.Mails.CheckAllMailTimings(now.AddTicks(-1));
        Assert.Same(source, Assert.Single(graph.Mails._allPlayerMails.Values));
        graph.Mails.CheckAllMailTimings(now);
        var returned = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.NotEqual(source.Id, returned.Id);
        Assert.True(returned.Header.Returned);
        Assert.Equal(graph.Sender.Id, returned.Header.ReceiverId);
        Assert.Equal(MailStatus.Unread, returned.Header.Status);
        Assert.Equal(now, returned.Body.RecvDate);
        Assert.Equal(123, returned.Body.CopperCoins);
        Assert.Same(item, Assert.Single(returned.Body.Attachments));
        Assert.Same(graph.Sender.Inventory.MailAttachments, item._holdingContainer);
        Assert.Equal(senderMoney, graph.Sender.Money);
        Assert.Equal(receiverMoney, graph.Receiver.Money);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id}"));
        Assert.Equal(returned.Id, Scalar($"SELECT returned_mail_id FROM mail_lifecycle WHERE mail_id={source.Id}"));
        graph.Mails.CheckAllMailTimings(now);
        Assert.False(graph.Mails.ReturnMail(graph.Sender, returned.Id));
        Assert.Single(graph.Mails._allPlayerMails);

        var (reloadedItems, reloadedMails) = graph.ReloadLifecycle();
        Assert.False(reloadedMails._allPlayerMails.ContainsKey(source.Id));
        Assert.True(reloadedMails._allPlayerMails[returned.Id].Header.Returned);
        Assert.Equal(graph.Sender.Id, reloadedItems.GetItemByItemId(item.Id).OwnerId);
        graph.Mails.CheckAllMailTimings(now + MailManager.MailExpireDelay);
        Assert.Empty(graph.Mails._allPlayerMails);
        Assert.Equal(2, Scalar($"SELECT outcome FROM mail_lifecycle WHERE mail_id={returned.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
    }

    [Theory]
    [InlineData(MailType.AucBidWin, false, false)]
    [InlineData(MailType.Demolish, false, false)]
    [InlineData(MailType.SysExpress, false, false)]
    [InlineData(MailType.Normal, true, false)]
    [InlineData(MailType.Express, false, true)]
    public void Expiry_ArchivePreservesEveryItemColumnAndAllAmounts_AfterRestart(MailType type, bool returned, bool missingSender)
    {
        using var graph = new SendGraph();
        var item = graph.AddEquipment(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 876, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        source.MailType = type;
        source.Header.Returned = returned;
        if (missingSender) source.Header.SenderId = 0x7ffffff0;
        source.Body.BillingAmount = 53;
        source.Body.MoneyAmount2 = 67;
        source.Header.Attachments = source.GetTotalAttachmentCount();
        source.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        item.Count = 1;
        item.SetFlag(ItemFlag.SoulBound);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        var originalRow = ItemRow(item.Id);
        var sourceSnapshot = MailLifecycleStore.Snapshot(source);

        graph.Mails.CheckAllMailTimings();
        graph.Mails.CheckAllMailTimings();

        Assert.Empty(graph.Mails._allPlayerMails);
        Assert.Null(graph.Items.GetItemByItemId(item.Id));
        Assert.DoesNotContain(item, graph.Receiver.Inventory.MailAttachments.Items);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(ItemRow(item.Id))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(TextScalar($"SELECT item_row FROM mail_archive_items WHERE item_id={item.Id}"))));
        Assert.Equal(4, JsonNode.Parse(TextScalar($"SELECT item_row FROM mail_archive_items WHERE item_id={item.Id}"))["grade"].GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sourceSnapshot), JsonNode.Parse(TextScalar($"SELECT source_mail FROM mail_lifecycle WHERE mail_id={source.Id}"))));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id}"));
        var (items, mails) = graph.ReloadLifecycle();
        Assert.Null(items.GetItemByItemId(item.Id));
        Assert.False(mails._allPlayerMails.ContainsKey(source.Id));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
    }

    [Theory]
    [InlineData(MailType.Normal, MailStatus.Unread)]
    [InlineData(MailType.Normal, MailStatus.Read)]
    [InlineData(MailType.AucOffSuccess, MailStatus.Unread)]
    [InlineData(MailType.Demolish, MailStatus.Read)]
    public void Expiry_EmptyMailRemovesOnlyActiveRow(MailType type, MailStatus status)
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 0));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        mail.MailType = type;
        mail.Header.Status = status;
        mail.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        graph.Mails.CheckAllMailTimings();
        Assert.Empty(graph.Mails._allPlayerMails);
        Assert.Equal(3, Scalar($"SELECT outcome FROM mail_lifecycle WHERE mail_id={mail.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE mail_id={mail.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_FullDestinationOrSaveFailure_RetainsSourceThenRetriesOnce(bool saveFailure)
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 99, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        source.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        var originalCommit = graph.Mails.CommitLifecycle;
        if (saveFailure) graph.Mails.CommitLifecycle = _ => false;
        else graph.Sender.Inventory.MailAttachments.ContainerSize = 0;
        graph.Mails.CheckAllMailTimings();
        Assert.Same(source, Assert.Single(graph.Mails._allPlayerMails.Values));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Same(graph.Receiver.Inventory.MailAttachments, item._holdingContainer);
        Assert.Equal(graph.Receiver.Id, Scalar($"SELECT owner FROM items WHERE id={item.Id}"));
        graph.Mails.CommitLifecycle = originalCommit;
        graph.Sender.Inventory.MailAttachments.ContainerSize = -1;
        graph.Mails.CheckAllMailTimings();
        graph.Mails.CheckAllMailTimings();
        Assert.Single(graph.Mails._allPlayerMails);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_LedgerInsertFailure_RollsBackAssetsRemovalAndArchive(bool archive)
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 123, 0));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        mail.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        if (archive) mail.MailType = MailType.Demolish;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        var trigger = $"mail_lifecycle_fail_{mail.Id}";
        var table = archive ? "mail_archive_items" : "mail_lifecycle";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN IF NEW.mail_id={mail.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected lifecycle failure'; END IF; END");
        try
        {
            graph.Mails.CheckAllMailTimings();
            Assert.Same(mail, Assert.Single(graph.Mails._allPlayerMails.Values));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={mail.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
            Assert.Equal(graph.Receiver.Id, Scalar($"SELECT owner FROM items WHERE id={item.Id}"));
            Assert.Same(graph.Receiver.Inventory.MailAttachments, item._holdingContainer);
        }
        finally { Execute($"DROP TRIGGER {trigger}"); }
        graph.Mails.CheckAllMailTimings();
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={mail.Id}"));
    }

    [Fact]
    public void ReturnPacket_ExactR208022BodyReturnsMail_NotSpam_AndRejectsForeignMail()
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 123));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        var receiverSession = new Mock<ISession>();
        graph.ConnectReceiver(receiverSession);
        receiverSession.Setup(session => session.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(bytes =>
        {
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={mail.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={mail.Id}"));
        });
        var packet = new CSReturnMailPacket { Connection = graph.Receiver.Connection };
        Assert.Equal((ushort)0xa3, packet.TypeId);
        Assert.Equal((byte)1, packet.Level);
        var foreignConnection = new GameConnection(new Mock<ISession>().Object) { ActiveChar = graph.Sender };
        var foreign = new CSReturnMailPacket { Connection = foreignConnection };
        var body = new PacketStream();
        body.Write(mail.Id);
        body.Rollback();
        foreign.Read(body);
        Assert.Same(mail, Assert.Single(graph.Mails._allPlayerMails.Values));
        body.Rollback();
        packet.Read(body);
        Assert.Equal(0, body.LeftBytes);
        var returned = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.True(returned.Header.Returned);
        Assert.Equal(123, returned.Body.CopperCoins);
        Assert.Equal(graph.Sender.Id, returned.Header.ReceiverId);
        var opcode = SCOffsets.SCMailReturnedPacket;
        receiverSession.Verify(session => session.SendPacket(It.Is<byte[]>(bytes => bytes.Length >= 8 &&
            bytes[6] == (byte)opcode && bytes[7] == (byte)(opcode >> 8))), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MailLifecycle_UnknownCommitOutcomeRequiresRestartWithoutRestoringPreparedAssets(bool commitFirst)
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 123, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        var commit = graph.Mails.CommitLifecycle;
        graph.Mails.CommitLifecycle = write =>
        {
            if (commitFirst) Assert.True(commit(write));
            throw new InvalidOperationException("Injected lost commit acknowledgement");
        };
        var stops = 0;
        graph.Mails.LifecycleCommitFailure = (_, _) => stops++;
        Assert.Throws<InvalidOperationException>(() => graph.Mails.ReturnMail(graph.Receiver, source.Id));
        Assert.Equal(1, stops);
        Assert.False(graph.Mails._allPlayerMails.ContainsKey(source.Id));
        Assert.Same(graph.Sender.Inventory.MailAttachments, item._holdingContainer);
        var (_, loaded) = graph.ReloadLifecycle();
        Assert.Equal(!commitFirst, loaded._allPlayerMails.ContainsKey(source.Id));
        Assert.Equal(commitFirst ? 1 : 0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
    }

    [Fact]
    public async Task MailLifecycle_ConcurrentManualReturnCommitsOnlyOneReplacement()
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 123, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => graph.Mails.ReturnMail(graph.Receiver, source.Id), TestContext.Current.CancellationToken)));
        Assert.Equal(1, results.Count(result => result));
        Assert.Single(graph.Mails._allPlayerMails);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Same(graph.Sender.Inventory.MailAttachments, item._holdingContainer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    public void ReturnPacket_RejectsTruncatedOrTrailingBodyBeforeAccessingSession(int length)
    {
        var packet = new CSReturnMailPacket();
        var body = new PacketStream(new byte[length]);
        packet.Read(body);
        Assert.Equal(0, body.Pos);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ReturnPacket_RejectsNonpositiveIdBeforeAccessingSession(long id)
    {
        var body = new PacketStream();
        body.Write(id);
        body.Rollback();
        new CSReturnMailPacket().Read(body);
        Assert.Equal(0, body.LeftBytes);
    }

    private static string TextScalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar();
    }

    [Fact]
    public void Return_UsesSenderIdentityEvenWhenStoredNameIsOld()
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 123));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        mail.Header.SenderName = "OldSenderName";
        Assert.True(graph.Mails.ReturnMail(graph.Receiver, mail.Id));
        var returned = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.Equal(graph.Sender.Name, returned.ReceiverName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifecycle_ForeignOrSharedAttachmentNeverMovesOrArchives(bool shared)
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 123, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        if (shared)
        {
            var other = new BaseMail { Id = source.Id + 99,
                Header = { ReceiverId = graph.Receiver.Id }, Body = { Attachments = [item] } };
            graph.Mails._allPlayerMails.TryAdd(other.Id, other);
        }
        else item.OwnerId = graph.Sender.Id;
        Assert.False(graph.Mails.ReturnMail(graph.Receiver, source.Id));
        source.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        source.MailType = MailType.SysExpress;
        graph.Mails.CheckAllMailTimings();
        Assert.True(graph.Mails._allPlayerMails.ContainsKey(source.Id));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Same(graph.Receiver.Inventory.MailAttachments, item._holdingContainer);
        Assert.NotNull(graph.Items.GetItemByItemId(item.Id));
    }

    private static string ItemRow(ulong id)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT items.*, grade + 0 AS numeric_grade FROM items WHERE id={id}";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var row = new Dictionary<string, object>();
        for (var i = 0; i < reader.FieldCount - 1; i++)
        {
            var valueIndex = reader.GetName(i) == "grade" ? reader.FieldCount - 1 : i;
            row[reader.GetName(i)] = reader.IsDBNull(valueIndex) ? null : reader.GetValue(valueIndex);
        }
        return JsonSerializer.Serialize(row);
    }

    [Fact]
    public void Expiry_SenderDeletedDuringProcess_ArchivesDespiteStaleNameCache()
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 77, 0));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        Execute($"UPDATE characters SET deleted=1 WHERE id={graph.Sender.Id}");
        Assert.Equal(graph.Sender.Id, NameManager.Instance.GetCharacterId(graph.Sender.Name));
        Assert.False(graph.Mails.ReturnMail(graph.Receiver, mail.Id));
        mail.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        graph.Mails.CheckAllMailTimings();
        Assert.Equal(2, Scalar($"SELECT outcome FROM mail_lifecycle WHERE mail_id={mail.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
        Assert.Empty(graph.Mails._allPlayerMails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterRemoval_ReturnsContentsRegardlessOfDeliveryOrExpiry(bool expired)
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Normal, 77));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        mail.Body.RecvDate = expired ? DateTime.UtcNow.AddDays(-15) : DateTime.UtcNow.AddDays(1);
        Assert.False(graph.Mails.ReturnMail(graph.Receiver, mail.Id));
        Assert.True(mail.ReturnToSender());
        var returned = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.True(returned.Header.Returned);
        Assert.Equal(graph.Sender.Id, returned.Header.ReceiverId);
        Assert.Equal(77, returned.Body.CopperCoins);
    }

    [Fact]
    public void Expiry_UnavailableSenderReadDefersWithoutArchive()
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 77));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        mail.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        graph.Mails.ActiveMailSender = _ => null;
        graph.Mails.CheckAllMailTimings();
        Assert.Same(mail, Assert.Single(graph.Mails._allPlayerMails.Values));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={mail.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_SenderDeletionOrRestorationBetweenChecks_RetriesWithCurrentPolicy(bool initiallyDeleted)
    {
        using var graph = new SendGraph();
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 77));
        var mail = Assert.Single(graph.Mails._allPlayerMails.Values);
        mail.Body.RecvDate = DateTime.UtcNow.AddDays(-15);
        Execute($"UPDATE characters SET deleted={(initiallyDeleted ? 1 : 0)} WHERE id={graph.Sender.Id}");
        var commit = graph.Mails.CommitLifecycle;
        graph.Mails.CommitLifecycle = write =>
        {
            Execute($"UPDATE characters SET deleted={(initiallyDeleted ? 0 : 1)} WHERE id={graph.Sender.Id}");
            return commit(write);
        };
        graph.Mails.CheckAllMailTimings();
        Assert.Same(mail, Assert.Single(graph.Mails._allPlayerMails.Values));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={mail.Id}"));
        graph.Mails.CommitLifecycle = commit;
        graph.Mails.CheckAllMailTimings();
        Assert.Equal(initiallyDeleted ? 1 : 2, Scalar($"SELECT outcome FROM mail_lifecycle WHERE mail_id={mail.Id}"));
    }

    [Fact]
    public void ReturnedMail_ManualClaimAndDelete_NeverRecycleEitherLifecycleIdAcrossRestart()
    {
        using var graph = new SendGraph(useRealMailIds: true);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 77));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.True(graph.Mails.ReturnMail(graph.Receiver, source.Id));
        var returned = Assert.Single(graph.Mails._allPlayerMails.Values);
        Assert.True(graph.Sender.Mails.GetAttached(returned.Id, true, false, false));
        graph.Sender.Mails.DeleteMail(returned.Id, false);
        Assert.Empty(graph.Mails._allPlayerMails);
        Assert.NotEqual((uint)returned.Id, graph.MailAllocator.GetNextId());
        graph.MailAllocator.ReleaseId((uint)source.Id);
        Assert.NotEqual((uint)source.Id, graph.MailAllocator.GetNextId());
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={returned.Id}"));
        Assert.Equal(returned.Id, Scalar($"SELECT returned_mail_id FROM mail_lifecycle WHERE mail_id={source.Id}"));

        var restarted = new MailIdManager();
        Assert.True(restarted.Initialize());
        restarted.ReleaseId((uint)source.Id);
        restarted.ReleaseId((uint)returned.Id);
        var next = restarted.GetNextId();
        Assert.NotEqual((uint)source.Id, next);
        Assert.NotEqual((uint)returned.Id, next);
    }
}
