using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

[NotInParallel]
public sealed class HousingSalePlanTests
{
    private static readonly DateTime UtcNow = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    [Arguments(2u, HousingPermission.Private)]
    [Arguments(2u, HousingPermission.Family)]
    [Arguments(2u, HousingPermission.Guild)]
    [Arguments(2u, HousingPermission.Public)]
    [Arguments(3u, HousingPermission.Private)]
    public async Task ValidateListing_NonOwnerIncludingOriginalBuilder_IsRejected(uint callerId, HousingPermission permission)
    {
        var house = CreateHouse();
        house.CoOwnerId = 2;
        house.Permission = permission;
        var caller = CreateCharacter(callerId);
        caller.AccountId = house.AccountId;

        var error = HousingSalePlan.ValidateListing(house, caller, 100, 0, true, UtcNow);

        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotSellAsNotOwner);
        await Assert.That(house.SellPrice).IsEqualTo(0u);
    }

    [Test]
    public async Task ValidateListing_NullCaller_DoesNotAuthorizeFreeAdministrativeListing()
    {
        var error = HousingSalePlan.ValidateListing(CreateHouse(), null, 100, 0, true, UtcNow);

        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotSellAsNotOwner);
    }

    [Test]
    [Arguments(0u, ErrorMessageType.HouseCannotSellAsTooLowPrice)]
    [Arguments(2147483648u, ErrorMessageType.MailTooMuchMoney)]
    [Arguments(uint.MaxValue, ErrorMessageType.MailTooMuchMoney)]
    public async Task ValidateListing_UnrepresentablePrice_IsRejected(uint price, ErrorMessageType expected)
    {
        var error = HousingSalePlan.ValidateListing(CreateHouse(), CreateCharacter(1), price, 0, true, UtcNow);

        await Assert.That(error).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1u)]
    [Arguments((uint)int.MaxValue)]
    public async Task ValidateListing_CheckedPositivePrice_IsAccepted(uint price)
    {
        var error = HousingSalePlan.ValidateListing(CreateHouse(), CreateCharacter(1), price, 0, true, UtcNow);

        await Assert.That(error).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    [Arguments(1u, true, ErrorMessageType.HouseCannotSellToOneself)]
    [Arguments(2u, false, ErrorMessageType.HouseCannotSellAsDesignatedBuyerNotFound)]
    [Arguments(2u, true, ErrorMessageType.NoErrorMessage)]
    public async Task ValidateListing_DesignatedBuyer_MustExistAndDifferFromSeller(uint buyerId, bool exists, ErrorMessageType expected)
    {
        var error = HousingSalePlan.ValidateListing(CreateHouse(), CreateCharacter(1), 100, buyerId, exists, UtcNow);

        await Assert.That(error).IsEqualTo(expected);
    }

    [Test]
    public async Task ValidateListing_AlreadyListed_RejectsReplacementWithoutNewCertificateCharge()
    {
        var house = CreateHouse();
        house.SellPrice = 100;
        house.SellToPlayerId = 2;

        var error = HousingSalePlan.ValidateListing(house, CreateCharacter(1), 200, 3, true, UtcNow);

        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotSellAsAlreadyForSale);
        await Assert.That(house.SellPrice).IsEqualTo(100u);
        await Assert.That(house.SellToPlayerId).IsEqualTo(2u);
    }

    [Test]
    public async Task ValidateCancellation_OriginalBuilderCannotCancelCurrentOwnersListing()
    {
        var house = CreateHouse();
        house.CoOwnerId = 2;
        house.SellPrice = 100;

        var error = HousingSalePlan.ValidateCancellation(house, CreateCharacter(2));

        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotCancelSellAsNotOwner);
        await Assert.That(house.SellPrice).IsEqualTo(100u);
    }

    [Test]
    public async Task ValidateCancellation_ReplayedAfterListingCleared_IsRejected()
    {
        var error = HousingSalePlan.ValidateCancellation(CreateHouse(), CreateCharacter(1));

        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotCancelSellAsNotForSale);
    }

    [Test]
    public async Task TryCreate_InsufficientFunds_DoesNotProduceOwnershipChange()
    {
        var house = CreateHouse();
        house.SellPrice = 100;
        var buyer = CreateCharacter(2);
        buyer.Money = 99;

        var accepted = HousingSalePlan.TryCreate(house, buyer, 100, UtcNow, out var plan, out var error);

        await Assert.That(accepted).IsFalse();
        await Assert.That(plan).IsNull();
        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotBuyAsNotEnoughMoney);
        await Assert.That(house.OwnerId).IsEqualTo(1u);
        await Assert.That(house.SellPrice).IsEqualTo(100u);
        await Assert.That(buyer.Money).IsEqualTo(99L);
    }

    [Test]
    public async Task TryCreate_DifferentDesignatedBuyer_IsRejected()
    {
        var house = CreateHouse();
        house.SellPrice = 100;
        house.SellToPlayerId = 3;

        var accepted = HousingSalePlan.TryCreate(house, CreateCharacter(2), 100, UtcNow, out _, out var error);

        await Assert.That(accepted).IsFalse();
        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotBuyAsNotDesignatedBuyer);
    }

    [Test]
    [Arguments(0u)]
    [Arguments(99u)]
    [Arguments(2147483648u)]
    [Arguments(uint.MaxValue)]
    public async Task TryCreate_StaleOrUnrepresentablePrice_IsRejected(uint offeredPrice)
    {
        var house = CreateHouse();
        house.SellPrice = 100;

        var accepted = HousingSalePlan.TryCreate(house, CreateCharacter(2), offeredPrice, UtcNow, out _, out var error);

        await Assert.That(accepted).IsFalse();
        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseCannotBuyAsSaleInfoChanged);
    }

    [Test]
    public async Task TryCreate_ExpiredHouse_IsRejectedBeforeOwnershipChanges()
    {
        var house = CreateHouse();
        house.SellPrice = 100;
        house.ProtectionEndDate = UtcNow;

        var accepted = HousingSalePlan.TryCreate(house, CreateCharacter(2), 100, UtcNow, out _, out var error);

        await Assert.That(accepted).IsFalse();
        await Assert.That(error).IsEqualTo(ErrorMessageType.InvalidHouseInfo);
    }

    [Test]
    [Arguments(false, HousingPermission.Private)]
    [Arguments(true, HousingPermission.Public)]
    public async Task PurchasedState_PreservesPaidTaxTimeAndClearsListing(bool alwaysPublic, HousingPermission expectedPermission)
    {
        var house = CreateHouse(alwaysPublic);
        house.SellPrice = 100;
        house.SellToPlayerId = 2;
        house.IsDirty = false;
        var buyer = CreateCharacter(2);
        var protectionEnd = house.ProtectionEndDate;

        var accepted = HousingSalePlan.TryCreate(house, buyer, 100, UtcNow, out var plan, out _);
        plan.PurchasedState.Apply(house);

        await Assert.That(accepted).IsTrue();
        await Assert.That(house.OwnerId).IsEqualTo(buyer.Id);
        await Assert.That(house.AccountId).IsEqualTo(buyer.AccountId);
        await Assert.That(house.Permission).IsEqualTo(expectedPermission);
        await Assert.That(house.ProtectionEndDate).IsEqualTo(protectionEnd);
        await Assert.That(house.SellPrice).IsEqualTo(0u);
        await Assert.That(house.SellToPlayerId).IsEqualTo(0u);
        await Assert.That(house.IsDirty).IsTrue();

        plan.PreviousState.Apply(house);
        await Assert.That(house.OwnerId).IsEqualTo(1u);
        await Assert.That(house.AccountId).IsEqualTo(10u);
        await Assert.That(house.SellPrice).IsEqualTo(100u);
        await Assert.That(house.SellToPlayerId).IsEqualTo(2u);
        await Assert.That(house.ProtectionEndDate).IsEqualTo(protectionEnd);
        await Assert.That(house.IsDirty).IsFalse();
    }

    private static House CreateHouse(bool alwaysPublic = false)
    {
        return new House
        {
            Id = 42,
            TlId = 7,
            AccountId = 10,
            OwnerId = 1,
            CoOwnerId = 1,
            Name = "Test House",
            Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance },
            Template = new HousingTemplate { IsSellable = true, AlwaysPublic = alwaysPublic, HousingBindingDoodad = [] },
            CurrentStep = -1,
            ProtectionEndDate = UtcNow.AddDays(14)
        };
    }

    private static Character CreateCharacter(uint id)
    {
        return new Character(new UnitCustomModelParams())
        {
            Id = id,
            AccountId = id * 10,
            Name = $"Character{id}",
            Money = 1000,
            Faction = new SystemFaction { Id = FactionsEnum.HaranyaAlliance }
        };
    }
}
