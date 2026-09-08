using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class TradeManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockTradeId = Mock.Of<ITradeIdManager>();
        var mockWorld = Mock.Of<IWorldManager>();
        var mockItems = Mock.Of<IItemManager>();
        var mockSave = Mock.Of<ISaveManager>();
        var manager = new TradeManager(mockTradeId.Object, mockWorld.Object, mockItems.Object, mockSave.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockTradeId);
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockItems);
        Mock.VerifyNoOtherCalls(mockSave);
    }
}
