using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Taxations;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public class HousingManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockObjectId = Mock.Of<IObjectIdManager>();
        var mockFaction = Mock.Of<IFactionManager>();
        var mockLocale = Mock.Of<ILocalizationManager>();
        var mockWorld = Mock.Of<IWorldManager>();
        var mockTask = Mock.Of<ITaskManager>();
        var mockSkill = Mock.Of<ISkillManager>();
        var mockHousingId = Mock.Of<IHousingIdManager>();
        var mockHousingTld = Mock.Of<IHousingTldManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockName = Mock.Of<INameManager>();
        var mockZone = Mock.Of<IZoneManager>();
        var mockDoodad = Mock.Of<IDoodadManager>();
        var mockUcc = Mock.Of<IUccManager>();

        var manager = new HousingManager(
            mockObjectId.Object,
            mockFaction.Object,
            mockLocale.Object,
            mockWorld.Object,
            mockTask.Object,
            mockSkill.Object,
            mockHousingId.Object,
            mockHousingTld.Object,
            mockItem.Object,
            mockMail.Object,
            mockName.Object,
            mockZone.Object,
            mockDoodad.Object,
            mockUcc.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockObjectId);
        Mock.VerifyNoOtherCalls(mockFaction);
        Mock.VerifyNoOtherCalls(mockLocale);
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockTask);
        Mock.VerifyNoOtherCalls(mockSkill);
        Mock.VerifyNoOtherCalls(mockHousingId);
        Mock.VerifyNoOtherCalls(mockHousingTld);
        Mock.VerifyNoOtherCalls(mockItem);
        Mock.VerifyNoOtherCalls(mockMail);
        Mock.VerifyNoOtherCalls(mockName);
        Mock.VerifyNoOtherCalls(mockZone);
        Mock.VerifyNoOtherCalls(mockDoodad);
        Mock.VerifyNoOtherCalls(mockUcc);
    }

    [Test]
    public async Task CalculateBuildingTaxInfo_FirstStructure_ChargesDepositAndFirstWeekTax()
    {
        var manager = CreateManager();
        var template = new HousingTemplate
        {
            Taxation = new Taxation { Tax = 100000 }
        };

        var result = manager.CalculateBuildingTaxInfo(
            42,
            template,
            true,
            out var totalTaxToPay,
            out var heavyHouseCount,
            out var normalHouseCount,
            out var hostileTaxRate,
            out var oneWeekTaxCount);

        await Assert.That(result).IsTrue();
        await Assert.That(totalTaxToPay).IsEqualTo(300000);
        await Assert.That(oneWeekTaxCount).IsEqualTo(100000);
        await Assert.That(heavyHouseCount).IsEqualTo(0);
        await Assert.That(normalHouseCount).IsEqualTo(1);
        await Assert.That(hostileTaxRate).IsEqualTo(0);
    }

    [Test]
    public async Task IsTaxPrepaymentAllowed_AtConfiguredBoundary_IsAllowed()
    {
        var utcNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var result = HousingManager.IsTaxPrepaymentAllowed(utcNow.AddDays(35), utcNow, 7, 5);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsTaxPrepaymentAllowed_BeyondConfiguredBoundary_IsRejected()
    {
        var utcNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var result = HousingManager.IsTaxPrepaymentAllowed(utcNow.AddDays(35).AddTicks(1), utcNow, 7, 5);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsTaxPrepaymentAllowed_WhenDisabled_IsRejected()
    {
        var utcNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var result = HousingManager.IsTaxPrepaymentAllowed(utcNow, utcNow, 7, 0);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsTaxPrepaymentAllowed_DefaultWindow_OffersFiveFollowupPeriods()
    {
        var utcNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var taxDueDateAfterCurrentPayment = utcNow.AddDays(7);
        var offeredPeriods = 0;

        while (HousingManager.IsTaxPrepaymentAllowed(taxDueDateAfterCurrentPayment, utcNow, 7, 5))
        {
            offeredPeriods++;
            taxDueDateAfterCurrentPayment = taxDueDateAfterCurrentPayment.AddDays(7);
        }

        await Assert.That(offeredPeriods).IsEqualTo(5);
    }

    [Test]
    public async Task TaxDueDate_UsesConfiguredTaxPeriod()
    {
        var originalWorldConfig = AppConfiguration.Instance.World;
        var protectionEndDate = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 3 };
            var house = new House { ProtectionEndDate = protectionEndDate };

            await Assert.That(house.TaxDueDate).IsEqualTo(protectionEndDate.AddDays(-3));
        }
        finally
        {
            AppConfiguration.Instance.World = originalWorldConfig;
        }
    }

    [Test]
    public async Task ApplyTaxInfo_PrepaymentMail_HasClientTaxFieldsForNextPeriod()
    {
        var originalWorldConfig = AppConfiguration.Instance.World;
        var utcNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 7 };
            var house = new House
            {
                Id = 42,
                OwnerId = 7,
                Name = "Test House",
                ProtectionEndDate = utcNow.AddDays(14),
                Template = new HousingTemplate
                {
                    HeavyTax = true,
                    Taxation = new Taxation { Tax = 250000 }
                }
            };
            var mail = new MailForTax(house);

            MailForTax.ApplyTaxInfo(mail, house, "Owner", 25, 500000, 3, 1, 50, utcNow);

            await Assert.That(MailForTax.IsTaxMail(mail)).IsTrue();
            await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unpaid);
            await Assert.That(mail.Header.ReceiverId).IsEqualTo(house.OwnerId);
            await Assert.That(mail.ReceiverName).IsEqualTo("Owner");
            await Assert.That(mail.Title).IsEqualTo("title(25)");
            await Assert.That(mail.Header.Extra).IsEqualTo(((long)25 << 48) + house.Id);
            await Assert.That(mail.Body.BillingAmount).IsEqualTo(500000);
            await Assert.That(mail.Body.Text).Contains(Helpers.UnixTime(house.TaxDueDate).ToString());
            await Assert.That(mail.Body.Text).Contains(Helpers.UnixTime(house.ProtectionEndDate).ToString());
            await Assert.That(mail.Body.Text).Contains("'0', '500000', 'true', '1'");
        }
        finally
        {
            AppConfiguration.Instance.World = originalWorldConfig;
        }
    }

    private static HousingManager CreateManager()
    {
        return new HousingManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IMailManager>().Object,
            Mock.Of<INameManager>().Object,
            Mock.Of<IZoneManager>().Object,
            Mock.Of<IDoodadManager>().Object,
            Mock.Of<IUccManager>().Object);
    }
}
