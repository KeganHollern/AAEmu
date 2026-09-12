using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.World;

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

    [Test]
    public async Task GetHouseAtLocation_UsesInstanceAlleyAndCurrentModelHeight()
    {
        var worlds = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var singleton = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        singleton.SetValue(null, worlds);
        try
        {
            var manager = CreateManager();
            var worldTemplate = new WorldTemplate();
            var world = new WorldInstance(worldTemplate, 0, true, 0);
            var otherInstance = new WorldInstance(worldTemplate, 1, false, 0);
            var template = new HousingTemplate
            {
                MainModelId = 1, GardenRadius = 4, Alley = 1, ExtraHeightBelow = 2, ExtraHeightAbove = 3
            };
            var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
            instances.TryAdd(world.Id, world);
            instances.TryAdd(otherInstance.Id, otherInstance);
            var house = new House { Id = 7, Template = template, ParentWorld = world };
            house.Transform.Local.SetPosition(100, 100, 50);
            manager.GeometryAssets = new HousingGeometryAssets(_ => null, (_, _) => []);
            var models = (ConcurrentDictionary<uint, CryGeometryAsset>)typeof(HousingGeometryAssets)
                .GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager.GeometryAssets)!;
            models[1] = new CryGeometryAsset(new CryBounds(new Vector3(-2, -2, 0), new Vector3(2, 2, 10)), []);
            var houses = (Dictionary<uint, House>)typeof(HousingManager)
                .GetField("_houses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            houses.Add(house.Id, house);

            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(97, 97, 48))).IsSameReferenceAs(house);
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(102, 102, 62))).IsSameReferenceAs(house);
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(103, 100, 50))).IsNull();
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(100, 103, 50))).IsNull();
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(100, 100, 63))).IsNull();
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(96, 100, 50))).IsNull();
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(100, 100, 47))).IsNull();
            await Assert.That(manager.GetHouseAtLocation(otherInstance, new Vector3(100, 100, 50))).IsNull();

            house.Template = new HousingTemplate { MainModelId = 1, GardenRadius = 0 };
            await Assert.That(manager.GetHouseAtLocation(world, new Vector3(100, 100, 50))).IsNull();
        }
        finally
        {
            singleton.SetValue(null, previous);
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
