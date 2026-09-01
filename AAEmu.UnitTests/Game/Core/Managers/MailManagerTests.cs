using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class MailManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockMailId = Mock.Of<IMailIdManager>();
        var mockName = Mock.Of<INameManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockTask = Mock.Of<ITaskManager>();
        var mockWorld = Mock.Of<IWorldManager>();
        var mockHousing = Mock.Of<IHousingManager>();
        var mockLocale = Mock.Of<ILocalizationManager>();
        var manager = new MailManager(mockMailId.Object, mockName.Object, mockItem.Object, mockTask.Object, mockWorld.Object, new Lazy<IHousingManager>(() => mockHousing.Object), mockLocale.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockMailId);
        Mock.VerifyNoOtherCalls(mockName);
        Mock.VerifyNoOtherCalls(mockItem);
        Mock.VerifyNoOtherCalls(mockTask);
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockHousing);
        Mock.VerifyNoOtherCalls(mockLocale);
    }

    [Test]
    [Arguments(MailType.AucBidWin)]
    [Arguments(MailType.AucOffSuccess)]
    public async Task DeleteMail_AuctionClaimMail_DoesNotReleaseId(MailType mailType)
    {
        var mockMailId = Mock.Of<IMailIdManager>();
        var manager = CreateManager(mockMailId.Object);
        manager._allPlayerMails = [];
        const long mailId = 10000;
        manager.TryAddPlayerMail(new BaseMail { Id = mailId, MailType = mailType });

        var deleted = manager.DeleteMail(mailId);
        var deletedAgain = manager.DeleteMail(mailId);

        await Assert.That(deleted).IsTrue();
        await Assert.That(deletedAgain).IsFalse();
        await Assert.That(manager.AllPlayerMails.ContainsKey(mailId)).IsFalse();
        mockMailId.ReleaseId(Any<uint>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task DeleteMail_OrdinaryMail_ReleasesId()
    {
        var mockMailId = Mock.Of<IMailIdManager>();
        var manager = CreateManager(mockMailId.Object);
        manager._allPlayerMails = [];
        const long mailId = 10000;
        manager.TryAddPlayerMail(new BaseMail { Id = mailId, MailType = MailType.Normal });

        var deleted = manager.DeleteMail(mailId);

        await Assert.That(deleted).IsTrue();
        await Assert.That(manager.AllPlayerMails.ContainsKey(mailId)).IsFalse();
        mockMailId.ReleaseId((uint)mailId).WasCalled(Times.Once);
    }

    private static MailManager CreateManager(IMailIdManager mailIdManager)
    {
        return new MailManager(
            mailIdManager,
            Mock.Of<INameManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
    }
}
