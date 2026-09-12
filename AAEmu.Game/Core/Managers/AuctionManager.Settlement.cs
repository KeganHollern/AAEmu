using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.Game.Core.Managers;

public partial class AuctionManager
{
    private ErrorMessageType TryPostLotOnAuction(Character player, ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration)
    {
        if (!ValidPrices(startPrice, buyoutPrice, duration))
            return ErrorMessageType.AuctionInvalidStartPrice;

        var bag = player.Inventory?.Bag;
        var item = bag?.GetItemByItemId(itemId);
        if (player.Id == 0 || !IsHeldBy(item, bag, player.Id) ||
            !ReferenceEquals(itemManager.GetItemByItemId(itemId), item) ||
            item.Template == null || item.Count > item.Template.MaxCount ||
            item.HasFlag(ItemFlag.Secure) ||
            !item.CanDestroy())
            return ErrorMessageType.AuctionUpdateInventory;
        if (item.HasFlag(ItemFlag.SoulBound) ||
            item.Template.BindType is ItemBindType.BindOnPickup or ItemBindType.BindOnPickupPack)
            return ErrorMessageType.AucSoulBoundItem;

        var fee = ListingFee(buyoutPrice, duration);
        if (player.Money < fee)
            return ErrorMessageType.CanNotPutupMoney;

        var lot = CreateAuctionLot(player.Id, player.Name, item, startPrice, buyoutPrice, duration);
        if (lot == null || lot.Id == 0)
            return ErrorMessageType.AuctionInitFailed;

        using var transition = new AuctionTransition(this, lot, true);
        using var inventory = new InventoryMutation(ItemTaskType.Auction);
        using var mails = mailManager.BeginMutation();
        if (!inventory.TryChangeMoney(player, -fee) ||
            !inventory.TryMove(item, player.Inventory.AuctionAttachments) ||
            !transition.Add())
            return ErrorMessageType.AuctionUpdateInventory;

        if (!CommitSettlement(inventory, mails, transition, [player]))
            return ErrorMessageType.AuctionInitFailed;

        player.SendPacket(new SCAuctionPostedPacket(lot));
        return ErrorMessageType.NoErrorMessage;
    }

    private ErrorMessageType TryCancelAuctionLot(Character player, ulong auctionId)
    {
        if (!AuctionLots.TryGetValue(auctionId, out var lot) || lot.ClientId != player.Id ||
            lot.BidderId != 0 || lot.BidMoney != 0 || lot.EndTime <= DateTime.UtcNow || !IsEscrowed(lot))
            return ErrorMessageType.AuctionInvalidArticleForCancel;

        using var transition = new AuctionTransition(this, lot);
        using var inventory = new InventoryMutation(ItemTaskType.Auction);
        using var mails = mailManager.BeginMutation();
        if (!StageReturn(lot, true, inventory, mails) || !transition.Remove())
            return ErrorMessageType.AuctionInvalidArticleForCancel;
        if (!CommitSettlement(inventory, mails, transition, [player]))
            return ErrorMessageType.AuctionInitFailed;

        player.SendPacket(new SCAuctionCanceledPacket(lot));
        return ErrorMessageType.NoErrorMessage;
    }

    private ErrorMessageType TryBidOnAuctionLot(Character player, AuctionLot offeredLot, AuctionBid offeredBid)
    {
        if (player.Id == 0 || offeredLot == null || offeredBid == null || offeredLot.Id == 0 || offeredBid.LotId != offeredLot.Id ||
            !AuctionLots.TryGetValue(offeredLot.Id, out var lot) || lot.ClientId == player.Id ||
            lot.EndTime <= DateTime.UtcNow || !IsEscrowed(lot) || !ValidEscrowBid(lot))
            return ErrorMessageType.InvalidArticleForBidding;
        if (offeredBid.Money <= 0 || offeredBid.Money < lot.StartMoney || offeredBid.Money <= lot.BidMoney)
            return ErrorMessageType.InvalidPriceForBidding;

        var buyout = lot.DirectMoney > 0 && offeredBid.Money >= lot.DirectMoney;
        var price = buyout ? lot.DirectMoney : offeredBid.Money;
        var priorOwnBid = lot.BidderId == player.Id ? lot.BidMoney : 0;
        var debit = price - priorOwnBid;
        if (debit <= 0 || player.Money < debit)
            return ErrorMessageType.NotEnoughMoney;

        using var transition = new AuctionTransition(this, lot);
        using var inventory = new InventoryMutation(ItemTaskType.Auction);
        using var mails = mailManager.BeginMutation();
        if (!inventory.TryChangeMoney(player, -debit))
            return ErrorMessageType.NotEnoughMoney;
        if (lot.BidderId != 0 && lot.BidderId != player.Id && !StageBidRefund(lot, mails))
            return ErrorMessageType.AuctionInitFailed;

        if (buyout)
        {
            if (!StageSale(lot, player.Id, price, inventory, mails) || !transition.Remove())
                return ErrorMessageType.AuctionInitFailed;
        }
        else
        {
            lot.BidderId = player.Id;
            lot.BidderName = player.Name;
            lot.BidWorldId = (byte)player.Transform.WorldId;
            lot.BidMoney = price;
            lot.IsDirty = true;
        }

        if (!CommitSettlement(inventory, mails, transition, [player]))
            return ErrorMessageType.AuctionInitFailed;

        // The client-provided bidder identity and price echoes never become authoritative state.
        var acceptedBid = new AuctionBid
        {
            LotId = lot.Id, BidderId = player.Id, BidderName = player.Name,
            WorldId = (byte)player.Transform.WorldId, Money = price
        };
        player.SendPacket(new SCAuctionBidPacket(acceptedBid, buyout, lot.Item.TemplateId));
        return ErrorMessageType.NoErrorMessage;
    }

    private bool TryExpireAuctionLot(AuctionLot lot)
    {
        // Revalidate a timer snapshot after any preceding settlement and its callbacks.
        if (!AuctionLots.TryGetValue(lot.Id, out var current) || !ReferenceEquals(lot, current) ||
            lot.EndTime > DateTime.UtcNow)
            return true;
        if (!IsEscrowed(lot) || !ValidEscrowBid(lot))
            return false;

        using var transition = new AuctionTransition(this, lot);
        using var inventory = new InventoryMutation(ItemTaskType.Auction);
        using var mails = mailManager.BeginMutation();
        var prepared = lot.BidderId == 0
            ? StageReturn(lot, false, inventory, mails)
            : StageSale(lot, lot.BidderId, lot.BidMoney, inventory, mails);
        return prepared && transition.Remove() && CommitSettlement(inventory, mails, transition, []);
    }

    private bool StageBidRefund(AuctionLot lot, MailMutation mails)
    {
        var mail = CreateAuctionMail(lot.BidderId, MailType.AucBidFail, ".auctionBidFail", "Failed Bid Notice",
            $"body('{MailItemName(lot)}')", lot.BidMoney);
        return mail != null && mails.TryAdd(mail);
    }

    private bool StageSale(AuctionLot lot, uint buyerId, int soldAmount, InventoryMutation inventory, MailMutation mails)
    {
        var share = (int)((long)soldAmount * 9 / 10);
        var tax = soldAmount - share;
        var sellerMail = CreateAuctionMail(lot.ClientId, MailType.AucOffSuccess, ".auctionOffSuccess", "Successful Auction Notice",
            $"body('{MailItemName(lot)}', {lot.Item.Count}, {share}, {soldAmount}, {tax}, {ListingFee(lot.DirectMoney, lot.Duration)})", share);
        var buyerMail = CreateAuctionMail(buyerId, MailType.AucBidWin, ".auctionBidWin", "Successful Purchase",
            $"body('{MailItemName(lot)}', {lot.Item.Count}, {soldAmount})");
        return sellerMail != null && buyerMail != null &&
            MoveToMail(lot.Item, buyerMail, inventory) && mails.TryAdd(sellerMail) && mails.TryAdd(buyerMail);
    }

    private bool StageReturn(AuctionLot lot, bool canceled, InventoryMutation inventory, MailMutation mails)
    {
        var mail = CreateAuctionMail(lot.ClientId,
            canceled ? MailType.AucOffCancel : MailType.AucOffFail,
            canceled ? ".auctionOffCancel" : ".auctionOffFail",
            canceled ? "Cancelled Auction Notice" : "Failed Auction Notice",
            $"body('{MailItemName(lot)}', {lot.Item.Count})");
        return mail != null && MoveToMail(lot.Item, mail, inventory) && mails.TryAdd(mail);
    }

    private BaseMail CreateAuctionMail(uint receiverId, MailType type, string senderName, string title, string text, int copper = 0)
    {
        var receiverName = nameManager.GetCharacterName(receiverId);
        if (receiverId == 0 || string.IsNullOrEmpty(receiverName) || copper < 0)
            return null;
        var now = DateTime.UtcNow;
        var mail = new BaseMail
        {
            MailType = type, ReceiverName = receiverName, Title = title,
            Header = { SenderId = 0, SenderName = senderName, ReceiverId = receiverId },
            Body = { Text = text, CopperCoins = copper, SendDate = now, RecvDate = now }
        };
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        return mail;
    }

    private bool MoveToMail(Item item, BaseMail mail, InventoryMutation inventory)
    {
        var container = itemManager.GetItemContainerForCharacter(mail.Header.ReceiverId, SlotType.Mail, null, 0);
        if (container == null || !inventory.TryMove(item, container))
            return false;
        mail.Body.Attachments.Add(item);
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        return true;
    }

    private bool CommitSettlement(InventoryMutation inventory, MailMutation mails, AuctionTransition transition,
        IReadOnlyCollection<Character> participants)
    {
        bool committed;
        try
        {
            committed = saveManager.Value.TryCommitEconomy(participants);
        }
        catch
        {
            // A commit exception is not a known rollback. Keep the prepared source and outputs together.
            transition.PreservePreparedState();
            inventory.PreservePreparedState();
            mails.PreservePreparedState();
            throw;
        }
        if (!committed)
            return false;

        try
        {
            transition.Complete();
            inventory.Complete();
            mails.Complete();
        }
        catch
        {
            // A notification failure must not roll back another participant that SQL already committed.
            transition.PreservePreparedState();
            inventory.PreservePreparedState();
            mails.PreservePreparedState();
            throw;
        }
        return true;
    }

    private bool IsEscrowed(AuctionLot lot)
    {
        var item = lot.Item;
        return ValidPrices(lot.StartMoney, lot.DirectMoney, lot.Duration) &&
            item?.Template != null && ReferenceEquals(itemManager.GetItemByItemId(item.Id), item) &&
            item._holdingContainer?.ContainerType == SlotType.Auction &&
            IsHeldBy(item, item._holdingContainer, lot.ClientId);
    }

    private static bool IsHeldBy(Item item, ItemContainer container, uint ownerId)
    {
        return ownerId != 0 && item != null && item.Id != 0 && item.Count > 0 && container != null &&
            item.OwnerId == ownerId && container.OwnerId == ownerId &&
            item._holdingContainer == container && item.SlotType == container.ContainerType &&
            container.Items.Count(existing => existing.Id == item.Id) == 1 && container.Items.Contains(item);
    }

    private static bool ValidPrices(int startPrice, int buyoutPrice, AuctionDuration duration)
    {
        return startPrice > 0 && buyoutPrice >= 0 && (buyoutPrice == 0 || buyoutPrice >= startPrice) &&
            duration is >= AuctionDuration.AuctionDuration6Hours and <= AuctionDuration.AuctionDuration48Hours;
    }

    private static bool ValidEscrowBid(AuctionLot lot)
    {
        return lot.BidderId == 0 ? lot.BidMoney == 0 :
            lot.BidderId != lot.ClientId && lot.BidMoney >= lot.StartMoney &&
            (lot.DirectMoney == 0 || lot.BidMoney < lot.DirectMoney);
    }

    private static int ListingFee(int buyoutPrice, AuctionDuration duration)
    {
        return (int)Math.Min(MaxListingFee, (long)buyoutPrice * ((int)duration + 1) / 100);
    }

    private string MailItemName(AuctionLot lot)
    {
        var name = localizationManager.Get("items", "name", lot.Item.TemplateId, $"Item:{lot.Item.TemplateId}");
        return name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }

    private void ReleaseAuctionId(ulong id) => auctionIdManager.ReleaseId((uint)id);

    /// <summary>Stages the auction row and deletion queue alongside inventory and mail mutations.</summary>
    private sealed class AuctionTransition(AuctionManager owner, AuctionLot lot, bool created = false) : IDisposable
    {
        private readonly byte _bidWorld = lot.BidWorldId;
        private readonly uint _bidderId = lot.BidderId;
        private readonly string _bidderName = lot.BidderName;
        private readonly int _bidMoney = lot.BidMoney;
        private readonly bool _dirty = lot.IsDirty;
        private readonly long[] _deleted = owner.DeletedAuctionItemIds.ToArray();
        private bool _removed;
        private bool _added;
        private bool _finished;

        public bool Add()
        {
            _added = owner.AuctionLots.TryAdd(lot.Id, lot);
            return _added;
        }

        public bool Remove()
        {
            if (!owner.AuctionLots.TryGetValue(lot.Id, out var current) || !ReferenceEquals(current, lot))
                return false;
            _removed = owner.AuctionLots.TryRemove(lot.Id, out _);
            if (_removed && !owner.DeletedAuctionItemIds.Contains((long)lot.Id))
                owner.DeletedAuctionItemIds.Add((long)lot.Id);
            return _removed;
        }

        public void Complete()
        {
            // Keep published IDs reserved for this server lifetime. A replayed request
            // must not act on a different listing that reused a settled lot's ID.
            _finished = true;
        }

        public void PreservePreparedState() => _finished = true;

        public void Dispose()
        {
            if (_finished)
                return;
            _finished = true;
            lot.BidWorldId = _bidWorld;
            lot.BidderId = _bidderId;
            lot.BidderName = _bidderName;
            lot.BidMoney = _bidMoney;
            lot.IsDirty = _dirty;
            if (_removed)
                owner.AuctionLots[lot.Id] = lot;
            if (_added)
                owner.AuctionLots.TryRemove(lot.Id, out _);
            owner.DeletedAuctionItemIds.Clear();
            foreach (var id in _deleted)
                owner.DeletedAuctionItemIds.Add(id);
            if (created && !owner.AuctionLots.ContainsKey(lot.Id))
                owner.ReleaseAuctionId(lot.Id);
        }
    }
}
