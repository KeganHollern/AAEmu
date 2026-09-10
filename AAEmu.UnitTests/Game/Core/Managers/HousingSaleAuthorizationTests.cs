using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class HousingSaleAuthorizationTests
{
    [Test]
    public async Task SaleActions_InvalidLink_ReturnFalseWithoutDereferencingHouse()
    {
        var manager = CreateManager();
        var caller = CreateCharacter(1);

        await Assert.That(manager.SetForSale(99, 100, 0, caller)).IsFalse();
        await Assert.That(manager.CancelForSale(99, caller)).IsFalse();
        await Assert.That(manager.BuyHouse(99, 100, caller)).IsFalse();
        await Assert.That(caller.Money).IsEqualTo(1000L);
    }

    [Test]
    public async Task SaleActions_NullCaller_CannotUseCommandBypass()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);

        await Assert.That(manager.SetForSale(house.TlId, 100, 0, null)).IsFalse();
        house.SellPrice = 100;
        await Assert.That(manager.CancelForSale(house.TlId, null)).IsFalse();
        await Assert.That(manager.BuyHouse(house.TlId, 100, null)).IsFalse();
        await Assert.That(house.OwnerId).IsEqualTo(1u);
        await Assert.That(house.SellPrice).IsEqualTo(100u);
    }

    [Test]
    public async Task SaleActions_RecycledLinkMapping_IsRejected()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);
        var staleLink = house.TlId;
        house.TlId = 8;
        var links = GetField<Dictionary<ushort, House>>(manager, "_housesTl");
        links[house.TlId] = house;

        await Assert.That(manager.SetForSale(staleLink, 100, 0, CreateCharacter(1))).IsFalse();
        house.SellPrice = 100;
        await Assert.That(manager.CancelForSale(staleLink, CreateCharacter(1))).IsFalse();
        await Assert.That(manager.BuyHouse(staleLink, 100, CreateCharacter(2))).IsFalse();
        await Assert.That(house.SellPrice).IsEqualTo(100u);
    }

    [Test]
    public async Task SaleActions_RemovedDatabaseIdentity_IsRejected()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);
        GetField<Dictionary<uint, House>>(manager, "_houses").Clear();

        await Assert.That(manager.SetForSale(house, 100, 0, CreateCharacter(1))).IsFalse();
        house.SellPrice = 100;
        await Assert.That(manager.CancelForSale(house, CreateCharacter(1))).IsFalse();
        await Assert.That(manager.BuyHouse(house.TlId, 100, CreateCharacter(2))).IsFalse();
    }

    [Test]
    [Arguments(HousingPermission.Private)]
    [Arguments(HousingPermission.Family)]
    [Arguments(HousingPermission.Guild)]
    [Arguments(HousingPermission.Public)]
    public async Task ListAndCancel_OriginalBuilderOnSameAccount_HasNoSaleAuthority(HousingPermission permission)
    {
        var manager = CreateManager();
        var house = CreateHouse();
        house.CoOwnerId = 2;
        house.Permission = permission;
        RegisterHouse(manager, house);
        var originalBuilder = CreateCharacter(2);
        originalBuilder.AccountId = house.AccountId;

        await Assert.That(manager.SetForSale(house.TlId, 100, 2, originalBuilder)).IsFalse();
        house.SellPrice = 100;
        house.SellToPlayerId = 3;
        await Assert.That(manager.CancelForSale(house.TlId, originalBuilder)).IsFalse();
        await Assert.That(house.SellPrice).IsEqualTo(100u);
        await Assert.That(house.SellToPlayerId).IsEqualTo(3u);
    }

    [Test]
    [Arguments(0u)]
    [Arguments(2147483648u)]
    [Arguments(uint.MaxValue)]
    public async Task SetForSale_InvalidPrice_DoesNotReachInventory(uint price)
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);

        await Assert.That(manager.SetForSale(house.TlId, price, 0, CreateCharacter(1))).IsFalse();
        await Assert.That(house.SellPrice).IsEqualTo(0u);
    }

    [Test]
    public async Task SetForSale_InvalidDesignatedBuyer_DoesNotConsumeCertificates()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);

        await Assert.That(manager.SetForSale(house.TlId, 100, 99, CreateCharacter(1))).IsFalse();
        await Assert.That(house.SellPrice).IsEqualTo(0u);
    }

    [Test]
    public async Task BuyHouse_InsufficientFunds_DoesNotDebitOrClearListing()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        house.SellPrice = 100;
        RegisterHouse(manager, house);
        var buyer = CreateCharacter(2);
        buyer.Money = 99;

        await Assert.That(manager.BuyHouse(house.TlId, 100, buyer)).IsFalse();
        await Assert.That(buyer.Money).IsEqualTo(99L);
        await Assert.That(house.OwnerId).IsEqualTo(1u);
        await Assert.That(house.SellPrice).IsEqualTo(100u);
    }

    [Test]
    public async Task BuyHouse_AnotherDesignatedBuyer_DoesNotDebitOrClearListing()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        house.SellPrice = 100;
        house.SellToPlayerId = 3;
        RegisterHouse(manager, house);
        var buyer = CreateCharacter(2);

        await Assert.That(manager.BuyHouse(house.TlId, 100, buyer)).IsFalse();
        await Assert.That(buyer.Money).IsEqualTo(1000L);
        await Assert.That(house.SellToPlayerId).IsEqualTo(3u);
    }

    [Test]
    public async Task BuyHouse_UnlistedOrAlreadySold_DoesNotDebitBuyer()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);
        var buyer = CreateCharacter(2);

        await Assert.That(manager.BuyHouse(house.TlId, 100, buyer)).IsFalse();
        await Assert.That(buyer.Money).IsEqualTo(1000L);
    }

    [Test]
    public async Task OwnerActions_AfterOwnershipTransfer_PreviousOwnerCannotChangeProperty()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        house.SellPrice = 100;
        RegisterHouse(manager, house);
        var previousOwner = CreateCharacter(1);
        var buyer = CreateCharacter(2);
        var oldConnection = new GameConnection(null) { ActiveChar = previousOwner };
        var protectionEnd = house.ProtectionEndDate;
        HousingSalePlan.TryCreate(house, buyer, 100, DateTime.UtcNow, out var plan, out _);
        lock (house.TaxPaymentSyncRoot)
            plan.PurchasedState.Apply(house);

        manager.ChangeHousePermission(oldConnection, house.TlId, HousingPermission.Public);
        manager.ChangeHouseName(oldConnection, house.TlId, "stale owner name");
        manager.Demolish(oldConnection, house, false, false);

        await Assert.That(house.OwnerId).IsEqualTo(buyer.Id);
        await Assert.That(house.Permission).IsEqualTo(HousingPermission.Private);
        await Assert.That(house.Name).IsEqualTo("Test House");
        await Assert.That(house.ProtectionEndDate).IsEqualTo(protectionEnd);
        await Assert.That(GetField<List<uint>>(manager, "_removedHousings")).IsEmpty();
    }

    [Test]
    public async Task Demolish_StaleTaxExpiryAfterPayment_PreservesPaidProperty()
    {
        var manager = CreateManager();
        var house = CreateHouse();
        RegisterHouse(manager, house);
        var protectionEnd = house.ProtectionEndDate;

        manager.Demolish(null, house, true, false);

        await Assert.That(house.OwnerId).IsEqualTo(1u);
        await Assert.That(house.ProtectionEndDate).IsEqualTo(protectionEnd);
        await Assert.That(GetField<List<uint>>(manager, "_removedHousings")).IsEmpty();
    }

    private static House CreateHouse()
    {
        return new House
        {
            Id = 42,
            TlId = 7,
            AccountId = 10,
            OwnerId = 1,
            CoOwnerId = 1,
            Name = "Test House",
            Template = new HousingTemplate { IsSellable = true, HousingBindingDoodad = [] },
            CurrentStep = -1,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
    }

    private static Character CreateCharacter(uint id)
    {
        return new Character(new UnitCustomModelParams())
        {
            Id = id,
            AccountId = id * 10,
            Money = 1000,
            Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance }
        };
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

    private static T GetField<T>(HousingManager manager, string name)
    {
        return (T)typeof(HousingManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
    }

    private static void RegisterHouse(HousingManager manager, House house)
    {
        GetField<Dictionary<uint, House>>(manager, "_houses").Add(house.Id, house);
        GetField<Dictionary<ushort, House>>(manager, "_housesTl").Add(house.TlId, house);
    }
}
