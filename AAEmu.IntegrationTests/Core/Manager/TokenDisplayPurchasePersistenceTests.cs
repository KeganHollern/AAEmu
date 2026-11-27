using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public void CottageDesignPurchase_SavesOnlyThePaidDesignAndRemainingStars(int count)
    {
        var ids = new Mock<IItemIdManager>();
        using var graph = new SendGraph(itemIds: ids.Object);
        var buyer = graph.Sender;
        ids.Setup(manager => manager.GetNextId()).Returns(buyer.Id + 50);
        var templates = HousingField<Dictionary<uint, ItemTemplate>>(graph.Items, "_templates");
        templates[21166] = new ItemTemplate { Id = 21166, Price = 100000, MaxCount = 1, FixedGrade = -1 };
        var stars = graph.Register(new Item(buyer.Id + 10UL, new ItemTemplate
            { Id = 23633, MaxCount = 1000, FixedGrade = -1 }, count), 0);
        Assert.True(graph.Save.TryCommitEconomy([buyer]));
        var purchase = new DoodadFuncPurchase { Id = 134, ItemId = 21166, Count = 1, CoinItemId = 23633, CoinCount = 15 };
        lock (buyer.StorePurchaseSyncRoot)
            Assert.Equal(count < 15 ? ErrorMessageType.NotEnoughItem : ErrorMessageType.NoErrorMessage, purchase.TryPurchase(buyer));
        var design = buyer.Inventory.Bag.Items.SingleOrDefault(item => item.TemplateId == 21166);
        Assert.Equal(count >= 15, design != null);
        Assert.True(graph.Save.DoSave());
        var reloaded = graph.ReloadLifecycle().Items;
        var savedStars = reloaded.GetItemByItemId(stars.Id);
        if (count == 15)
            Assert.Null(savedStars);
        else
            Assert.Equal(count < 15 ? count : count - 15, savedStars.Count);
        if (design != null)
        {
            var savedDesign = reloaded.GetItemByItemId(design.Id);
            Assert.Equal(21166u, savedDesign.TemplateId);
            Assert.Equal(1, savedDesign.Count);
            Assert.Equal(buyer.Id, savedDesign.OwnerId);
            Assert.Equal(SlotType.Inventory, savedDesign.SlotType);
        }
        Assert.Equal(buyer.Money, Scalar($"SELECT money FROM characters WHERE id={buyer.Id}"));
    }
}
