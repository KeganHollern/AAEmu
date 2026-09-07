using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// The validated property changes for one purchase. Inventory, mail, and persistence
/// enlist these changes while holding the house's persistence gate.
/// </summary>
internal sealed class HousingSalePlan
{
    public House House { get; }
    public Character Buyer { get; }
    public int Price { get; }
    public HousingSaleState PreviousState { get; }
    public HousingSaleState PurchasedState { get; }

    private HousingSalePlan(House house, Character buyer, int price)
    {
        House = house;
        Buyer = buyer;
        Price = price;
        PreviousState = HousingSaleState.Capture(house);
        PurchasedState = PreviousState with
        {
            AccountId = buyer.AccountId,
            OwnerId = buyer.Id,
            CoOwnerId = buyer.Id,
            Permission = house.Template.AlwaysPublic ? HousingPermission.Public : HousingPermission.Private,
            Faction = buyer.Faction,
            SellPrice = 0,
            SellToPlayerId = 0,
            IsDirty = true
        };
    }

    public static ErrorMessageType ValidateListing(House house, Character seller, uint price, uint buyerId,
        bool buyerExists, DateTime utcNow)
    {
        if (!IsOwnedHouse(house))
            return ErrorMessageType.InvalidHouseInfo;
        // CoOwnerId is the historical builder field, not a second sale authority.
        if (seller == null || seller.Id == 0 || seller.Id != house.OwnerId)
            return ErrorMessageType.HouseCannotSellAsNotOwner;
        if (!house.Template.IsSellable)
            return ErrorMessageType.HouseCannotSellAsType;
        if (house.CurrentStep != -1)
            return ErrorMessageType.HouseCannotSellAsUnderConstruction;
        if (house.ProtectionEndDate <= utcNow)
            return ErrorMessageType.HouseCannotSellAsDelayedTax;
        if (house.SellPrice != 0 || house.SellToPlayerId != 0)
            return ErrorMessageType.HouseCannotSellAsAlreadyForSale;
        if (price == 0)
            return ErrorMessageType.HouseCannotSellAsTooLowPrice;
        if (price > int.MaxValue)
            return ErrorMessageType.MailTooMuchMoney;
        if (buyerId == seller.Id)
            return ErrorMessageType.HouseCannotSellToOneself;
        if (buyerId != 0 && !buyerExists)
            return ErrorMessageType.HouseCannotSellAsDesignatedBuyerNotFound;
        return ErrorMessageType.NoErrorMessage;
    }

    public static ErrorMessageType ValidateCancellation(House house, Character seller)
    {
        if (!IsOwnedHouse(house))
            return ErrorMessageType.InvalidHouseInfo;
        if (seller == null || seller.Id == 0 || seller.Id != house.OwnerId)
            return ErrorMessageType.HouseCannotCancelSellAsNotOwner;
        if (house.SellPrice == 0)
            return ErrorMessageType.HouseCannotCancelSellAsNotForSale;
        return ErrorMessageType.NoErrorMessage;
    }

    public static bool TryCreate(House house, Character buyer, uint offeredPrice, DateTime utcNow,
        out HousingSalePlan plan, out ErrorMessageType error)
    {
        plan = null;
        error = ValidatePurchase(house, buyer, offeredPrice, utcNow);
        if (error != ErrorMessageType.NoErrorMessage)
            return false;

        plan = new HousingSalePlan(house, buyer, checked((int)offeredPrice));
        return true;
    }

    private static ErrorMessageType ValidatePurchase(House house, Character buyer, uint offeredPrice, DateTime utcNow)
    {
        if (!IsOwnedHouse(house) || buyer == null || buyer.Id == 0 || buyer.AccountId == 0 || buyer.Faction == null ||
            house.ProtectionEndDate <= utcNow)
            return ErrorMessageType.InvalidHouseInfo;
        if (!house.Template.IsSellable || house.CurrentStep != -1 || house.SellPrice == 0)
            return ErrorMessageType.HouseCannotBuyAsNotForSale;
        if (offeredPrice == 0 || offeredPrice > int.MaxValue || offeredPrice != house.SellPrice)
            return ErrorMessageType.HouseCannotBuyAsSaleInfoChanged;
        if (house.OwnerId == buyer.Id)
            return ErrorMessageType.HouseCannotBuyAsOwner;
        if (house.SellToPlayerId != 0 && house.SellToPlayerId != buyer.Id)
            return ErrorMessageType.HouseCannotBuyAsNotDesignatedBuyer;
        if (buyer.Money < offeredPrice)
            return ErrorMessageType.HouseCannotBuyAsNotEnoughMoney;
        return ErrorMessageType.NoErrorMessage;
    }

    private static bool IsOwnedHouse(House house)
    {
        return house is { Id: > 0, TlId: > 0, OwnerId: > 0, AccountId: > 0, Template: not null };
    }
}

internal sealed record HousingSaleState(
    uint AccountId,
    uint OwnerId,
    uint CoOwnerId,
    HousingPermission Permission,
    SystemFaction Faction,
    uint SellPrice,
    uint SellToPlayerId,
    DateTime ProtectionEndDate,
    bool IsDirty)
{
    public static HousingSaleState Capture(House house)
    {
        return new HousingSaleState(house.AccountId, house.OwnerId, house.CoOwnerId, house.Permission, house.Faction,
            house.SellPrice, house.SellToPlayerId, house.ProtectionEndDate, house.IsDirty);
    }

    public void Apply(House house)
    {
        house.AccountId = AccountId;
        house.OwnerId = OwnerId;
        house.CoOwnerId = CoOwnerId;
        house.Permission = Permission;
        house.Faction = Faction;
        house.SellPrice = SellPrice;
        house.SellToPlayerId = SellToPlayerId;
        house.ProtectionEndDate = ProtectionEndDate;
        house.IsDirty = IsDirty;
    }
}
