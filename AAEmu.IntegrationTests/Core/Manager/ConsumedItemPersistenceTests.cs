using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ConsumedItem_OnlyAnAcknowledgedRowDeletionSkipsTheNextDelete(bool saveBeforeCompletion, bool rowExists)
    {
        using var graph = new SendGraph();
        var item = graph.AddItem(0);
        var player = graph.Sender;
        if (rowExists)
            Assert.True(graph.Save.TryCommitEconomy([player]));
        Assert.Equal(rowExists ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));

        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.SkillReagents);
            Assert.True(mutation.TryConsume(player.Inventory.Bag, item, item.Count));
            if (saveBeforeCompletion)
                Assert.True(graph.Save.TryCommitEconomy([player]));
            Assert.True(mutation.Complete());
        }

        Assert.Null(graph.Items.GetItemByItemId(item.Id));
        Assert.Equal(rowExists && !saveBeforeCompletion ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
        var errors = ReadItemErrors(() => Assert.True(graph.Save.DoSave()));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id}"));
        Assert.Null(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        if (rowExists)
            Assert.Empty(errors);
        else
            Assert.Contains("only 0/1 items removed", Assert.Single(errors));
    }

    [Fact]
    public void ConsumedItem_QueuedRowsReportOneMissingRowOnlyOnce()
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        var item = graph.AddItem(0);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var missing = graph.AddItem(1);
        // The merchant's buyback flow can already have queued a source ID before a paid use.
        graph.Items.MarkItemForDbDeletion(item.Id);
        graph.Items.MarkItemForDbDeletion(missing.Id);
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.SkillReagents);
            Assert.True(mutation.TryConsume(player.Inventory.Bag, item, item.Count));
            var errors = ReadItemErrors(() => Assert.True(graph.Save.TryCommitEconomy([player])));
            Assert.Contains("only 1/2 items removed", Assert.Single(errors));
            Assert.True(mutation.Complete());
        }
        Assert.Empty(ReadItemErrors(() => Assert.True(graph.Save.DoSave())));
        Assert.Null(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
    }

    private static string[] ReadItemErrors(Action save)
    {
        var original = LogManager.Configuration;
        using var target = new MemoryTarget { Layout = "${message}" };
        var config = new LoggingConfiguration();
        config.AddRule(LogLevel.Error, LogLevel.Fatal, target, typeof(ItemManager).FullName);
        try
        {
            LogManager.Configuration = config;
            save();
            return target.Logs.ToArray();
        }
        finally
        {
            LogManager.Configuration = original;
        }
    }
}
