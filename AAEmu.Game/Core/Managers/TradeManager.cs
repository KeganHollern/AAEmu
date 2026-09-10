using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class TradeManager(ITradeIdManager tradeIdManager, IWorldManager worldManager,
    IItemManager itemManager, ISaveManager saveManager) : Singleton<TradeManager>, ITradeManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    // Server interaction rule; the protocol does not supply an authoritative range.
    internal const float InteractionRange = 5f;
    private const int MaxSettlementTasks = 30;
    private readonly Dictionary<uint, TradeState> _trades = [];
    private readonly Dictionary<uint, TradeState> _participants = [];
    private readonly Dictionary<uint, Invitation> _invitations = [];

    public void CanStartTrade(Character owner, Character target)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!Eligible(owner, target))
                return;
            if (_participants.ContainsKey(owner.ObjId) || _invitations.ContainsKey(owner.ObjId))
            {
                owner.SendErrorMessage(ErrorMessageType.OnTrading);
                return;
            }
            if (_participants.ContainsKey(target.ObjId) || _invitations.ContainsKey(target.ObjId))
            {
                owner.SendErrorMessage(ErrorMessageType.TargetOnTrading);
                return;
            }

            var invitation = new Invitation(owner, target);
            _invitations.Add(owner.ObjId, invitation);
            _invitations.Add(target.ObjId, invitation);
            target.SendPacket(new SCCanStartTradePacket(owner.ObjId));
        }
    }

    public void StartTrade(Character owner, Character target)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (owner == null || target == null ||
                !_invitations.TryGetValue(target.ObjId, out var invitation) ||
                !ReferenceEquals(invitation.Owner, owner) || !ReferenceEquals(invitation.Target, target))
                return;
            RemoveInvitation(invitation);
            if (!Eligible(owner, target) || _participants.ContainsKey(owner.ObjId) ||
                _participants.ContainsKey(target.ObjId))
                return;

            var id = tradeIdManager.GetNextId();
            if (id == 0 || _trades.ContainsKey(id))
                return;
            var trade = new TradeState(id, owner, target);
            trade.Reservation = new TradeReservation(() =>
            {
                if (_trades.TryGetValue(id, out var current) && ReferenceEquals(current, trade))
                    Cancel(trade, owner, 0);
            });
            _trades.Add(id, trade);
            _participants.Add(owner.ObjId, trade);
            _participants.Add(target.ObjId, trade);
            owner.SendPacket(new SCTradeStartedPacket(target.ObjId));
            target.SendPacket(new SCTradeStartedPacket(owner.ObjId));
        }
    }

    public void DeclineTrade(Character character, uint ownerObjId, int reason)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (character == null || !_invitations.TryGetValue(character.ObjId, out var invitation) ||
                !ReferenceEquals(invitation.Target, character) || invitation.Owner.ObjId != ownerObjId)
                return;
            RemoveInvitation(invitation);
            SendCancellation(invitation.Owner, new SCCannotStartTradePacket(character.ObjId, reason));
        }
    }

    public void CancelTrade(Character character, int reason)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (character == null)
                return;
            if (_participants.TryGetValue(character.ObjId, out var trade) && IsParticipant(trade, character))
                Cancel(trade, character, reason);
            if (_invitations.TryGetValue(character.ObjId, out var invitation) &&
                (ReferenceEquals(invitation.Owner, character) || ReferenceEquals(invitation.Target, character)))
            {
                RemoveInvitation(invitation);
                var other = ReferenceEquals(invitation.Owner, character) ? invitation.Target : invitation.Owner;
                SendCancellation(other, new SCCannotStartTradePacket(character.ObjId, reason));
            }
        }
    }

    public void AddItem(Character character, SlotType slotType, byte slot, int amount)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!TryGetTrade(character, out var trade))
                return;
            var item = slotType == SlotType.Inventory ? character.Inventory.Bag.GetItemBySlot(slot) : null;
            if (amount <= 0 || !ValidItem(character, item, slot) || item.Count < amount ||
                Offers(trade, character).Any(offer => offer.Item.Id == item.Id) ||
                !trade.Reservation.TryReserve(item, amount))
            {
                Cancel(trade, character, 0);
                return;
            }

            var offer = new Offer(item, amount, slot, Snapshot(item, amount));
            Offers(trade, character).Add(offer);
            ResetAgreement(trade);
            character.SendPacket(new SCTradeItemPutupPacket(slotType, slot, amount));
            Other(trade, character).SendPacket(new SCOtherTradeItemPutupPacket(item, amount));
        }
    }

    public void RemoveItem(Character character, SlotType slotType, byte slot)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!TryGetTrade(character, out var trade) || slotType != SlotType.Inventory)
                return;
            var offers = Offers(trade, character);
            var offer = offers.FirstOrDefault(entry => entry.Slot == slot);
            if (offer == null)
                return;
            if (!ValidOffer(character, offer))
            {
                Cancel(trade, character, 0);
                return;
            }

            offers.Remove(offer);
            trade.Reservation.Release(offer.Item);
            ResetAgreement(trade);
            character.SendPacket(new SCTradeItemTookdownPacket(slotType, slot));
            Other(trade, character).SendPacket(new SCOtherTradeItemTookdownPacket(offer.Item, offer.Amount));
        }
    }

    public void AddMoney(Character character, int moneyAmount)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!TryGetTrade(character, out var trade))
                return;
            if (moneyAmount < 0 || !trade.Reservation.TryReserve(character, moneyAmount))
            {
                Cancel(trade, character, 0);
                return;
            }

            if (ReferenceEquals(trade.Owner, character))
                trade.OwnerMoney = moneyAmount;
            else
                trade.TargetMoney = moneyAmount;
            ResetAgreement(trade);
            character.SendPacket(new SCTradeMoneyPutupPacket(moneyAmount));
            Other(trade, character).SendPacket(new SCOtherTradeMoneyPutupPacket(moneyAmount));
        }
    }

    public void LockTrade(Character character, bool locked)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!TryGetTrade(character, out var trade))
                return;
            if (!ValidateOffers(trade))
            {
                Cancel(trade, character, 0);
                return;
            }
            if (!locked)
            {
                ResetAgreement(trade);
                return;
            }
            if (ReferenceEquals(trade.Owner, character))
                trade.OwnerLocked = true;
            else
                trade.TargetLocked = true;
            SendLocks(trade);
        }
    }

    public void OkTrade(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!TryGetTrade(character, out var trade) || !trade.OwnerLocked || !trade.TargetLocked)
                return;
            if (!ValidateOffers(trade))
            {
                Cancel(trade, character, 0);
                return;
            }
            if (ReferenceEquals(trade.Owner, character))
                trade.OwnerConfirmed = true;
            else
                trade.TargetConfirmed = true;
            if (!trade.OwnerConfirmed || !trade.TargetConfirmed)
            {
                SendConfirmations(trade);
                return;
            }

            // Remove the trade and release its reservations before staging. No other
            // mutation can enter the persistence gate until settlement finishes.
            Retire(trade);
            if (!Settle(trade))
                SendCanceled(trade, character, 0);
        }
    }

    private bool Settle(TradeState trade)
    {
        using var inventory = new InventoryMutation(ItemTaskType.Trade);
        var checkpointCalled = false;
        try
        {
            IReadOnlyList<(Item Item, int Count, ItemContainer Destination)> moves =
                [.. trade.OwnerOffers.Select(offer => (offer.Item, offer.Amount, trade.Target.Inventory.Bag)),
                 .. trade.TargetOffers.Select(offer => (offer.Item, offer.Amount, trade.Owner.Inventory.Bag))];
            if (!inventory.TryExchange(moves) ||
                !MoveMoney(trade.Owner, trade.Target, trade.OwnerMoney, trade.TargetMoney, inventory))
                return false;

            var ownerTasks = inventory.GetTasks(trade.Owner);
            var targetTasks = inventory.GetTasks(trade.Target);
            if (ownerTasks.Count > MaxSettlementTasks || targetTasks.Count > MaxSettlementTasks)
                return false;

            checkpointCalled = true;
            if (!saveManager.TryCommitEconomy([trade.Owner, trade.Target]))
                return false;
            inventory.Complete(false);
            trade.Owner.SendPacket(new SCTradeMadePacket(ItemTaskType.Trade, [.. ownerTasks], []));
            trade.Target.SendPacket(new SCTradeMadePacket(ItemTaskType.Trade, [.. targetTasks], []));
            Logger.Info("Trade {0} committed between {1} and {2}.", trade.Id, trade.Owner.Id, trade.Target.Id);
            return true;
        }
        catch (Exception exception) when (!checkpointCalled)
        {
            Logger.Error(exception, "Could not prepare trade {0}.", trade.Id);
            return false;
        }
        catch
        {
            // Commit may have completed, or an observer may have thrown after it.
            // The retired trade must never restore or transfer these assets again.
            inventory.PreservePreparedState();
            throw;
        }
    }

    private static bool MoveMoney(Character owner, Character target, int ownerAmount, int targetAmount,
        InventoryMutation inventory)
    {
        return (ownerAmount == 0 || inventory.TryChangeMoney(owner, -ownerAmount)) &&
               (targetAmount == 0 || inventory.TryChangeMoney(target, -targetAmount)) &&
               (targetAmount == 0 || inventory.TryChangeMoney(owner, targetAmount)) &&
               (ownerAmount == 0 || inventory.TryChangeMoney(target, ownerAmount));
    }

    private bool TryGetTrade(Character character, out TradeState trade)
    {
        trade = null;
        if (character == null || !_participants.TryGetValue(character.ObjId, out trade) ||
            !IsParticipant(trade, character))
            return false;
        if (Eligible(trade.Owner, trade.Target))
            return true;
        Cancel(trade, character, 0);
        return false;
    }

    private bool Eligible(Character owner, Character target)
    {
        return Current(owner) && Current(target) && !ReferenceEquals(owner, target) && owner.Id != target.Id &&
               ReferenceEquals(owner.ParentWorld, target.ParentWorld) &&
               owner.Transform.WorldId == target.Transform.WorldId &&
               owner.Transform.InstanceId == target.Transform.InstanceId &&
               owner.GetRelationStateTo(target) == RelationState.Friendly &&
               target.GetRelationStateTo(owner) == RelationState.Friendly &&
               Vector3.Distance(owner.Transform.World.Position, target.Transform.World.Position) <= InteractionRange;
    }

    private bool Current(Character character)
    {
        return character != null && character.Id != 0 && character.ObjId != 0 &&
               character.IsOnline && !character.IsDead && !character.IsInDuel && !character.IsInBattle &&
               character.Craft?.IsCrafting != true && character.SkillTask == null && !Skill.IsExecuting(character) &&
               character.ParentWorld != null && character.Inventory?.Bag != null &&
               ReferenceEquals(character.Connection?.ActiveChar, character) &&
               ReferenceEquals(worldManager.GetCharacterByObjId(character.ObjId), character);
    }

    private bool ValidateOffers(TradeState trade)
    {
        return trade.OwnerMoney >= 0 && trade.TargetMoney >= 0 &&
               trade.Owner.Money >= trade.OwnerMoney && trade.Target.Money >= trade.TargetMoney &&
               TradeReservation.GetReservedMoney(trade.Owner) >= trade.OwnerMoney &&
               TradeReservation.GetReservedMoney(trade.Target) >= trade.TargetMoney &&
               trade.OwnerOffers.All(offer => ValidOffer(trade.Owner, offer)) &&
               trade.TargetOffers.All(offer => ValidOffer(trade.Target, offer));
    }

    private bool ValidOffer(Character character, Offer offer)
    {
        return ValidItem(character, offer.Item, offer.Slot) && offer.Amount > 0 && offer.Item.Count >= offer.Amount &&
               TradeReservation.GetReservedCount(offer.Item) >= offer.Amount &&
               offer.Snapshot.SequenceEqual(Snapshot(offer.Item, offer.Amount));
    }

    private bool ValidItem(Character character, Item item, byte slot)
    {
        return item != null && item.Id != 0 && item.Count > 0 && item.Template != null &&
               item.Template is not BodyPartTemplate && item is not BodyPart &&
               !item.HasFlag(ItemFlag.SoulBound) && item.CanDestroy() &&
               item.OwnerId == character.Id && item.SlotType == SlotType.Inventory && item.Slot == slot &&
               ReferenceEquals(item._holdingContainer, character.Inventory.Bag) &&
               character.Inventory.Bag.Items.Count(entry => entry.Id == item.Id) == 1 &&
               ReferenceEquals(character.Inventory.Bag.GetItemBySlot(slot), item) &&
               ReferenceEquals(itemManager.GetItemByItemId(item.Id), item);
    }

    private void Retire(TradeState trade)
    {
        if (!_trades.Remove(trade.Id))
            return;
        _participants.Remove(trade.Owner.ObjId);
        _participants.Remove(trade.Target.ObjId);
        trade.Reservation.Dispose();
        tradeIdManager.ReleaseId(trade.Id);
    }

    private void Cancel(TradeState trade, Character cause, int reason)
    {
        Retire(trade);
        SendCanceled(trade, cause, reason);
    }

    private static void SendCanceled(TradeState trade, Character cause, int reason)
    {
        var byOwner = ReferenceEquals(cause, trade.Owner);
        SendCancellation(trade.Owner, new SCTradeCanceledPacket(reason, byOwner));
        SendCancellation(trade.Target, new SCTradeCanceledPacket(reason, !byOwner));
    }

    private static void SendCancellation(Character character, GamePacket packet)
    {
        try
        {
            character.SendPacket(packet);
        }
        catch (Exception exception)
        {
            // A disconnected participant must not prevent logout cleanup or the
            // other participant's cancellation notification.
            Logger.Warn(exception, "Could not notify character {0} of trade cancellation.", character.Id);
        }
    }

    private void RemoveInvitation(Invitation invitation)
    {
        _invitations.Remove(invitation.Owner.ObjId);
        _invitations.Remove(invitation.Target.ObjId);
    }

    private static void ResetAgreement(TradeState trade)
    {
        var changed = trade.OwnerLocked || trade.TargetLocked || trade.OwnerConfirmed || trade.TargetConfirmed;
        trade.OwnerLocked = trade.TargetLocked = trade.OwnerConfirmed = trade.TargetConfirmed = false;
        if (changed)
        {
            SendLocks(trade);
            SendConfirmations(trade);
        }
    }

    private static void SendLocks(TradeState trade)
    {
        trade.Owner.SendPacket(new SCTradeLockUpdatePacket(trade.OwnerLocked, trade.TargetLocked));
        trade.Target.SendPacket(new SCTradeLockUpdatePacket(trade.TargetLocked, trade.OwnerLocked));
    }

    private static void SendConfirmations(TradeState trade)
    {
        trade.Owner.SendPacket(new SCTradeOkUpdatePacket(trade.OwnerConfirmed, trade.TargetConfirmed));
        trade.Target.SendPacket(new SCTradeOkUpdatePacket(trade.TargetConfirmed, trade.OwnerConfirmed));
    }

    private static bool IsParticipant(TradeState trade, Character character) =>
        ReferenceEquals(trade.Owner, character) || ReferenceEquals(trade.Target, character);

    private static Character Other(TradeState trade, Character character) =>
        ReferenceEquals(trade.Owner, character) ? trade.Target : trade.Owner;

    private static List<Offer> Offers(TradeState trade, Character character) =>
        ReferenceEquals(trade.Owner, character) ? trade.OwnerOffers : trade.TargetOffers;

    private static byte[] Snapshot(Item item, int amount) => item.Write(new PacketStream(), amount).GetBytes();

    private sealed record Invitation(Character Owner, Character Target);
    private sealed record Offer(Item Item, int Amount, byte Slot, byte[] Snapshot);

    private sealed class TradeState(uint id, Character owner, Character target)
    {
        public uint Id { get; } = id;
        public Character Owner { get; } = owner;
        public Character Target { get; } = target;
        public List<Offer> OwnerOffers { get; } = [];
        public List<Offer> TargetOffers { get; } = [];
        public TradeReservation Reservation { get; set; }
        public int OwnerMoney { get; set; }
        public int TargetMoney { get; set; }
        public bool OwnerLocked { get; set; }
        public bool TargetLocked { get; set; }
        public bool OwnerConfirmed { get; set; }
        public bool TargetConfirmed { get; set; }
    }
}
