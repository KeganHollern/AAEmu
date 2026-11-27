using AAEmu.Commons.Network;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Fact]
    public void FishTrophy_ReloadKeepsTheSizeShownOnTheOriginalItem()
    {
        using var graph = new SendGraph();
        var template = new ItemTemplate { Id = 28041, MaxCount = 1, FixedGrade = -1 };
        var trophy = new BigFish(graph.Sender.Id + 27UL, template, 1) { Weight = 138.5f, Length = 171f };
        graph.Register(trophy, 3);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var originalDetails = new PacketStream();
        trophy.WriteDetails(originalDetails);

        var items = graph.ReloadLifecycle().Items;
        var restored = Assert.IsType<BigFish>(items.GetItemByItemId(trophy.Id));
        Assert.Equal(trophy.Weight, restored.Weight);
        Assert.Equal(trophy.Length, restored.Length);
        Assert.Equal(trophy.CreateTime, restored.CreateTime);
        Assert.Equal(trophy.TemplateId, restored.TemplateId);
        Assert.Equal(trophy.Count, restored.Count);
        Assert.Equal(trophy.Slot, restored.Slot);
        Assert.Equal(trophy.OwnerId, restored.OwnerId);

        // A later save must keep the same measurements for the next Game restart.
        restored.IsDirty = true;
        using (var db = MySQL.CreateConnection())
        using (var transaction = db.BeginTransaction())
        {
            items.Save(db, transaction);
            transaction.Commit();
        }
        var next = Assert.IsType<BigFish>(graph.ReloadLifecycle().Items.GetItemByItemId(trophy.Id));
        var details = new PacketStream();
        next.WriteDetails(details);
        Assert.Equal(originalDetails.GetBytes(), details.GetBytes());
    }
}
