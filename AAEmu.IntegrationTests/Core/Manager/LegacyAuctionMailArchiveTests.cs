using System.Text.Json.Nodes;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAuctionExpiry_ArchivesOriginalRowOnceIncludingAfterRealStartup(bool reloadFirst)
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        var originalRow = ItemRow(item.Id);
        var originalContainer = item._holdingContainer.ContainerId;
        var sourceSnapshot = MailLifecycleStore.Snapshot(source);
        var items = graph.Items;
        var mails = graph.Mails;
        if (reloadFirst)
        {
            (items, mails) = graph.ReloadLifecycle(useReloadedCheckpoint: true);
            item = items.GetItemByItemId(item.Id);
            Assert.False(item.IsDirty);
            Assert.Equal((byte)4, item.Grade);
            Assert.Equal(graph.Receiver.Id, item.OwnerId);
            Assert.Equal(SlotType.Auction, item.SlotType);
            Assert.Equal(graph.Sender.Id, item._holdingContainer.OwnerId);
            sourceSnapshot = MailLifecycleStore.Snapshot(mails._allPlayerMails[source.Id]);
        }
        var container = item._holdingContainer;
        mails.CheckAllMailTimings();
        mails.CheckAllMailTimings();

        Assert.False(mails._allPlayerMails.ContainsKey(source.Id));
        Assert.Null(items.GetItemByItemId(item.Id));
        Assert.DoesNotContain(item, container.Items);
        Assert.Equal(2, Scalar($"SELECT outcome FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(ItemRow(item.Id))));
        var archive = JsonNode.Parse(TextScalar($"SELECT item_row FROM mail_archive_items WHERE item_id={item.Id}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), archive));
        Assert.Equal((int)SlotType.Auction, archive["slot_type"].GetValue<int>());
        Assert.Equal(originalContainer, archive["container_id"].GetValue<ulong>());
        Assert.Equal(4, archive["grade"].GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sourceSnapshot),
            JsonNode.Parse(TextScalar($"SELECT source_mail FROM mail_lifecycle WHERE mail_id={source.Id}"))));
        var (restartedItems, restartedMails) = graph.ReloadLifecycle();
        Assert.Null(restartedItems.GetItemByItemId(item.Id));
        Assert.False(restartedMails._allPlayerMails.ContainsKey(source.Id));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM item_containers WHERE container_id={originalContainer}"));
    }

    [Theory]
    [InlineData("mail_lifecycle")]
    [InlineData("mail_archive_items")]
    public void LegacyAuctionExpiry_WriteFailureRestoresSourceAndRetriesOnce(string table)
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        var originalRow = ItemRow(item.Id);
        var container = item._holdingContainer;
        var trigger = $"legacy_archive_fail_{source.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN IF NEW.mail_id={source.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected legacy archive failure'; END IF; END");
        try
        {
            graph.Mails.CheckAllMailTimings();
            Assert.Same(source, graph.Mails._allPlayerMails[source.Id]);
            Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
            Assert.Same(container, item._holdingContainer);
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(ItemRow(item.Id))));
        }
        finally { Execute($"DROP TRIGGER {trigger}"); }
        graph.Mails.CheckAllMailTimings();
        graph.Mails.CheckAllMailTimings();
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
    }

    [Theory]
    [InlineData("listing")]
    [InlineData("mailReceipt")]
    [InlineData("itemReceipt")]
    [InlineData("sharedMail")]
    [InlineData("duplicateReference")]
    [InlineData("foreignOwner")]
    [InlineData("missingContainer")]
    [InlineData("wrongContainer")]
    [InlineData("wrongSlot")]
    [InlineData("foreignReceiver")]
    [InlineData("futureExpiry")]
    [InlineData("differentType")]
    [InlineData("differentCount")]
    [InlineData("occupiedSlot")]
    public void LegacyAuctionExpiry_PersistedGuardFailureNeverOverwritesOrArchives(string fault)
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        var container = item._holdingContainer;
        Item occupiedSlotItem = null;
        switch (fault)
        {
            case "listing":
                Execute($"""
                    INSERT INTO auction_house (id,duration,item_id,stack_size,world_id,client_id,client_name,
                      start_money,direct_money,bid_world_id,bidder_id,bidder_name,bid_money,extra)
                    VALUES ({source.Id},1,{item.Id},1,0,{graph.Sender.Id},'Test seller',1,2,0,0,'',0,0)
                    """);
                break;
            case "mailReceipt":
                Execute($"INSERT INTO auction_mail_claims (mail_id,claim_type,receiver_id) VALUES ({source.Id},2,{graph.Receiver.Id})");
                break;
            case "itemReceipt":
                Execute($"INSERT INTO auction_mail_claims (mail_id,claim_type,receiver_id,item_id) VALUES ({source.Id + 1},1,{graph.Receiver.Id},{item.Id})");
                break;
            case "sharedMail":
                Execute($"""
                    INSERT INTO mails (id,type,status,title,text,sender_name,receiver_id,receiver_name,
                      open_date,send_date,received_date,returned,extra,money_amount_1,money_amount_2,money_amount_3,attachment0)
                    SELECT id+1,type,status,title,text,sender_name,receiver_id,receiver_name,
                      open_date,send_date,received_date,returned,extra,0,0,0,attachment0 FROM mails WHERE id={source.Id}
                    """);
                break;
            case "duplicateReference": Execute($"UPDATE mails SET attachment1={item.Id} WHERE id={source.Id}"); break;
            case "foreignOwner": Execute($"UPDATE items SET owner={graph.Sender.Id} WHERE id={item.Id}"); break;
            case "missingContainer": Execute($"DELETE FROM item_containers WHERE container_id={container.ContainerId}"); break;
            case "wrongContainer": Execute($"UPDATE item_containers SET slot_type=2 WHERE container_id={container.ContainerId}"); break;
            case "wrongSlot": Execute($"UPDATE items SET slot_type=5 WHERE id={item.Id}"); break;
            case "foreignReceiver": Execute($"UPDATE mails SET receiver_id={graph.Sender.Id} WHERE id={source.Id}"); break;
            case "futureExpiry": Execute($"UPDATE mails SET received_date=UTC_TIMESTAMP() WHERE id={source.Id}"); break;
            case "differentType": Execute($"UPDATE mails SET type=31 WHERE id={source.Id}"); break;
            case "differentCount": Execute($"UPDATE items SET count=count+1 WHERE id={item.Id}"); break;
            case "occupiedSlot":
                var other = graph.AddItem(1);
                occupiedSlotItem = other;
                Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
                Execute($"UPDATE items SET container_id={container.ContainerId},slot={item.Slot},slot_type=6 WHERE id={other.Id}");
                break;
        }
        var originalRow = ItemRow(item.Id);
        graph.Mails.CheckAllMailTimings();
        Assert.Same(source, graph.Mails._allPlayerMails[source.Id]);
        Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
        Assert.Same(container, item._holdingContainer);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(ItemRow(item.Id))));

        // Restore this test's injected fault in the disposable schema before other startup fixtures read it.
        if (fault == "listing") Execute($"DELETE FROM auction_house WHERE id={source.Id}");
        if (fault == "mailReceipt") Execute($"DELETE FROM auction_mail_claims WHERE mail_id={source.Id}");
        if (fault == "itemReceipt") Execute($"DELETE FROM auction_mail_claims WHERE mail_id={source.Id+1}");
        if (fault == "sharedMail") Execute($"DELETE FROM mails WHERE id={source.Id+1}");
        if (occupiedSlotItem != null) occupiedSlotItem.IsDirty = true;
        source.IsDirty = true;
        item.IsDirty = true;
        container.IsDirty = true;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        graph.Mails.CheckAllMailTimings();
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
    }

    [Fact]
    public void LegacyAuctionExpiry_RealStartupKeepsForeignPersistedOwnerAndDefers()
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        Execute($"UPDATE items SET owner={graph.Sender.Id} WHERE id={item.Id}");
        var (items, mails) = graph.ReloadLifecycle(useReloadedCheckpoint: true);
        var loaded = items.GetItemByItemId(item.Id);
        Assert.Equal(graph.Sender.Id, loaded.OwnerId);
        Assert.False(loaded.IsDirty);
        mails.CheckAllMailTimings();
        Assert.True(mails._allPlayerMails.ContainsKey(source.Id));
        Assert.Equal(graph.Sender.Id, Scalar($"SELECT owner FROM items WHERE id={item.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
    }

    [Theory]
    [InlineData("listing")]
    [InlineData("receipt")]
    [InlineData("mail")]
    [InlineData("itemRow")]
    public void LegacyAuctionExpiry_PostSaveConflictRollsBackAndThenRetries(string conflict)
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        var originalRow = ItemRow(item.Id);
        var commit = graph.Mails.CommitAuctionArchive;
        graph.Mails.CommitAuctionArchive = (validate, write) => graph.Save.TryCommitMailArchive(validate, context =>
        {
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = conflict switch
            {
                "listing" => $"""
                    INSERT INTO auction_house (id,duration,item_id,stack_size,world_id,client_id,client_name,
                      start_money,direct_money,bid_world_id,bidder_id,bidder_name,bid_money,extra)
                    VALUES ({source.Id},1,{item.Id},1,0,{graph.Sender.Id},'Test seller',1,2,0,0,'',0,0)
                    """,
                "receipt" => $"INSERT INTO auction_mail_claims (mail_id,claim_type,receiver_id,item_id) VALUES ({source.Id},1,{graph.Receiver.Id},{item.Id})",
                "mail" => $"""
                    INSERT INTO mails (id,type,status,title,text,sender_name,receiver_id,receiver_name,
                      open_date,send_date,received_date,returned,extra,money_amount_1,money_amount_2,money_amount_3,attachment0)
                    VALUES ({source.Id+1},16,0,'Test','Test','Test',{graph.Receiver.Id},'Test',
                      UTC_TIMESTAMP(),UTC_TIMESTAMP(),UTC_TIMESTAMP(),0,0,0,0,0,{item.Id})
                    """,
                _ => $"UPDATE items SET grade=7 WHERE id={item.Id}"
            };
            command.ExecuteNonQuery();
            write(context);
        });
        graph.Mails.CheckAllMailTimings();
        Assert.Same(source, graph.Mails._allPlayerMails[source.Id]);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={source.Id+1}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_lifecycle WHERE mail_id={source.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM auction_house WHERE item_id={item.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM auction_mail_claims WHERE item_id={item.Id}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(ItemRow(item.Id))));
        graph.Mails.CommitAuctionArchive = commit;
        graph.Mails.CheckAllMailTimings();
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("container")]
    [InlineData("type")]
    [InlineData("unexpired")]
    public void LegacyAuctionExpiry_UnsafeLiveStateDefersBeforeCheckpoint(string fault)
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        if (fault == "item") item.Count++;
        if (fault == "container") item._holdingContainer.IsDirty = true;
        if (fault == "type") source.MailType = MailType.Demolish;
        if (fault == "unexpired") source.Body.RecvDate = DateTime.UtcNow;
        var called = false;
        graph.Mails.CommitAuctionArchive = (_, _) => { called = true; return false; };
        Assert.False(graph.Mails.ReturnMail(graph.Receiver, source.Id));
        graph.Mails.CheckAllMailTimings();
        Assert.False(called);
        Assert.Same(source, graph.Mails._allPlayerMails[source.Id]);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAuctionExpiry_UnknownCommitResultStopsWithoutRestoringSource(bool commitFirst)
    {
        using var graph = new SendGraph();
        var (source, item) = CreateLegacyAuctionMail(graph);
        var originalRow = ItemRow(item.Id);
        var commit = graph.Mails.CommitAuctionArchive;
        graph.Mails.CommitAuctionArchive = (validate, write) =>
        {
            if (commitFirst) Assert.True(commit(validate, write));
            throw new InvalidOperationException("Injected lost archive acknowledgement");
        };
        var stops = 0;
        graph.Mails.LifecycleCommitFailure = (_, _) => stops++;
        Assert.Throws<InvalidOperationException>(() => graph.Mails.CheckAllMailTimings());
        Assert.Equal(1, stops);
        Assert.False(graph.Mails._allPlayerMails.ContainsKey(source.Id));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalRow), JsonNode.Parse(ItemRow(item.Id))));
        var (items, mails) = graph.ReloadLifecycle();
        Assert.Equal(!commitFirst, mails._allPlayerMails.ContainsKey(source.Id));
        Assert.Equal(!commitFirst, items.GetItemByItemId(item.Id) != null);
        Assert.Equal(commitFirst ? 1 : 0, Scalar($"SELECT COUNT(*) FROM mail_archive_items WHERE item_id={item.Id}"));
    }

    private static (BaseMail Source, Item Item) CreateLegacyAuctionMail(SendGraph graph)
    {
        var item = graph.AddEquipment(0);
        Assert.Equal(MailResult.Success, graph.Send(MailType.Express, 0, 0));
        var source = Assert.Single(graph.Mails._allPlayerMails.Values);
        source.MailType = MailType.AucBidWin;
        source.Body.RecvDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var auction = graph.Items.GetItemContainerForCharacter(graph.Sender.Id, SlotType.Auction, null, 0);
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var inventory = new InventoryMutation(ItemTaskType.Mail);
            Assert.True(inventory.TryMove(item, auction));
            item.OwnerId = graph.Receiver.Id;
            Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
            inventory.Complete();
        }
        Assert.False(item.IsDirty);
        Assert.False(auction.IsDirty);
        Assert.Equal((long)SlotType.Auction, Scalar($"SELECT slot_type FROM items WHERE id={item.Id}"));
        return (source, item);
    }
}
