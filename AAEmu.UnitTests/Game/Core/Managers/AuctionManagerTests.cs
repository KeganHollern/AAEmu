using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class AuctionManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockItem = Mock.Of<IItemManager>();
        var mockName = Mock.Of<INameManager>();
        var mockAuctionId = Mock.Of<IAuctionIdManager>();
        var mockLocale = Mock.Of<ILocalizationManager>();
        var mockTask = Mock.Of<ITaskManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockSave = Mock.Of<ISaveManager>();
        var manager = new AuctionManager(mockItem.Object, mockName.Object, mockAuctionId.Object, mockLocale.Object,
            mockTask.Object, mockMail.Object, new Lazy<ISaveManager>(() => mockSave.Object));

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockItem);
        Mock.VerifyNoOtherCalls(mockName);
        Mock.VerifyNoOtherCalls(mockAuctionId);
        Mock.VerifyNoOtherCalls(mockLocale);
        Mock.VerifyNoOtherCalls(mockTask);
        Mock.VerifyNoOtherCalls(mockMail);
        Mock.VerifyNoOtherCalls(mockSave);
    }
}
