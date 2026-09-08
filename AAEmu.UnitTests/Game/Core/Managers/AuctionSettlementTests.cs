using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class AuctionSettlementTests
{
    private static readonly FieldInfo s_items = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_quests = typeof(Singleton<QuestManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private ItemManager _previousItems;
    private QuestManager _previousQuests;
    private ItemManager _items;
    private AuctionManager _auctions;
    private MailManager _mails;
    private Mock<IAuctionIdManager> _auctionIds;
    private Mock<IMailIdManager> _mailIds;
    private Dictionary<ulong, Item> _allItems;
    private Dictionary<uint, ItemTemplate> _templates;
    private Dictionary<ulong, ItemContainer> _containers;
    private readonly Dictionary<uint, RecordingSession> _sessions = [];
    private CharacterMock _seller;
    private CharacterMock _buyer;
    private CharacterMock _other;
    private Func<bool> _checkpoint;
    private int _commits;

    [Before(Test)]
    public void SetUp()
    {
        _previousItems = (ItemManager)s_items.GetValue(null);
        _previousQuests = (QuestManager)s_quests.GetValue(null);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        s_items.SetValue(null, _items);
        s_quests.SetValue(null, new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        _templates = [];
        _containers = [];
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_templates", _templates);
        SetField(_items, "_removedItems", new List<ulong>());
        SetField(_items, "_allPersistentContainers", _containers);
        _seller = Character(7, "Seller");
        _buyer = Character(8, "Buyer");
        _other = Character(9, "Other");
        var names = new NameManager();
        names.Load([], [], []);
        var world = Mock.Of<IWorldManager>();
        foreach (var player in new[] { _seller, _buyer, _other })
        {
            names.AddCharacter(player.Id, player.Name, 1);
            world.GetCharacter(player.Name).Returns(player);
            world.GetCharacterById(player.Id).Returns(player);
        }
        _mailIds = Mock.Of<IMailIdManager>();
        uint mailId = 10000;
        _mailIds.GetNextId().Returns(() => mailId++);
        var localization = Mock.Of<ILocalizationManager>();
        localization.Get("items", "name", 100, "Item:100").Returns("Auction item");
        _mails = new MailManager(_mailIds.Object, names, _items, Mock.Of<ITaskManager>().Object,
            world.Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            localization.Object) { _allPlayerMails = [] };
        SetField(_mails, "_deletedMailIds", new List<long>());
        _auctionIds = Mock.Of<IAuctionIdManager>();
        uint auctionId = 1000;
        _auctionIds.GetNextId().Returns(() => auctionId++);
        var saves = Mock.Of<ISaveManager>();
        _checkpoint = () => true;
        saves.TryCommitEconomy(Any<IReadOnlyCollection<Character>>(), Any<Action<PersistenceSaveContext>>())
            .Returns(() => { _commits++; return _checkpoint(); });
        _auctions = new AuctionManager(_items, names, _auctionIds.Object, localization.Object,
            Mock.Of<ITaskManager>().Object, _mails, new Lazy<ISaveManager>(() => saves.Object));
    }

    [After(Test)]
    public void TearDown()
    {
        s_items.SetValue(null, _previousItems);
        s_quests.SetValue(null, _previousQuests);
    }

    [Test]
    public async Task Post_PersistsFeeAndExactEscrowBeforeAcknowledgingAndRejectsRepeatedSource()
    {
        var item = Item();
        var prepared = false;
        _checkpoint = () =>
        {
            prepared = Monitor.IsEntered(SaveManager.PersistenceSyncRoot) && _seller.Money == 9980 &&
                _auctions.AuctionLots.Values.Single().Item == item && item.SlotType == SlotType.Auction &&
                !_seller.Inventory.Bag.Items.Contains(item) && _sessions.Values.All(session => session.Packets.Count == 0);
            return true;
        };
        Post(item);
        Post(item);
        await Assert.That(prepared).IsTrue();
        await Assert.That(_commits).IsEqualTo(1);
        await Assert.That(_seller.Money).IsEqualTo(9980L);
        await Assert.That(_auctions.AuctionLots.Count).IsEqualTo(1);
        await Assert.That(Packets(_seller, SCOffsets.SCAuctionPostedPacket)).IsEqualTo(1);
        await Assert.That(_allItems.Values.Single()).IsSameReferenceAs(item);
    }

    [Test]
    [Arguments("foreign")]
    [Arguments("stale")]
    [Arguments("duplicate")]
    [Arguments("bound")]
    [Arguments("secure")]
    [Arguments("oversize")]
    public async Task InvalidSource_CannotCreateListingOrChargeFee(string invalid)
    {
        var item = Item();
        switch (invalid)
        {
            case "foreign": item.OwnerId = _other.Id; break;
            case "stale": _seller.Inventory.Bag.Items.Remove(item); break;
            case "duplicate": _seller.Inventory.Bag.Items.Add(item); break;
            case "bound": item.ItemFlags |= ItemFlag.SoulBound; break;
            case "secure": item.ItemFlags |= ItemFlag.Secure; break;
            case "oversize": item.Count = item.Template.MaxCount + 1; break;
        }
        Post(item);
        await Assert.That(_commits).IsEqualTo(0);
        await Assert.That(_seller.Money).IsEqualTo(10000L);
        await Assert.That(_auctions.AuctionLots).IsEmpty();
    }

    [Test]
    [Arguments(0, 100, 0)]
    [Arguments(-1, 100, 0)]
    [Arguments(100, -1, 0)]
    [Arguments(100, 99, 0)]
    [Arguments(100, 100, 4)]
    public async Task InvalidPricesAndDuration_AreRejectedBeforeAllocation(int start, int buyout, byte duration)
    {
        var item = Item();
        _auctions.PostLotOnAuction(_seller, 0, 0, item.Id, start, buyout, (AuctionDuration)duration);
        await Assert.That(_commits).IsEqualTo(0);
        _auctionIds.GetNextId().WasCalled(Times.Never);
    }

    [Test]
    public async Task MaximumPrice_UsesWideFeeArithmeticAndClampsToOneMillion()
    {
        var item = Item();
        _seller.Money = 2_000_000;
        _auctions.PostLotOnAuction(_seller, 0, 0, item.Id, int.MaxValue, int.MaxValue, AuctionDuration.AuctionDuration48Hours);
        await Assert.That(_seller.Money).IsEqualTo(1_000_000L);
        await Assert.That(_auctions.AuctionLots.Values.Single().StartMoney).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task FailedPostCheckpoint_RestoresFeeOriginalSlotAndDirtyFlags()
    {
        var item = Item();
        item.IsDirty = false;
        var slot = item.Slot;
        _checkpoint = () => false;
        Post(item);
        await Assert.That(_seller.Money).IsEqualTo(10000L);
        await Assert.That(_seller.Inventory.Bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Slot).IsEqualTo(slot);
        await Assert.That(item.IsDirty).IsFalse();
        await Assert.That(_auctions.AuctionLots).IsEmpty();
        await Assert.That(Packets(_seller, SCOffsets.SCAuctionPostedPacket)).IsEqualTo(0);
        _auctionIds.ReleaseId(1000).WasCalled(Times.Once);
    }

    [Test]
    public async Task Bids_UseAuthoritativeLotAndRefundOnlyDisplacedBidder()
    {
        var lot = Listed();
        Bid(_buyer, lot, 500);
        Bid(_buyer, lot, 800);
        await Assert.That(_buyer.Money).IsEqualTo(9200L);
        await Assert.That(_mails._allPlayerMails).IsEmpty();
        Bid(_other, lot, 1000);
        var refund = _mails._allPlayerMails.Values.Single();
        await Assert.That(refund.MailType).IsEqualTo(MailType.AucBidFail);
        await Assert.That(refund.Header.ReceiverId).IsEqualTo(_buyer.Id);
        await Assert.That(refund.Body.CopperCoins).IsEqualTo(800);
        await Assert.That(_other.Money).IsEqualTo(9000L);
        await Assert.That(lot.BidderId).IsEqualTo(_other.Id);
        await Assert.That(lot.BidderName).IsEqualTo(_other.Name);
        await Assert.That(lot.BidMoney).IsEqualTo(1000);
        await Assert.That(lot.Item.SlotType).IsEqualTo(SlotType.Auction);
    }

    [Test]
    public async Task Buyout_CapsClientOfferAndCommitsExactItemPairedProceedsAndPriorRefund()
    {
        var lot = Listed();
        var item = lot.Item;
        item.Detail = [8, 6, 7, 5, 3, 0, 9];
        item.UccId = 55;
        item.Grade = 6;
        item.ImageItemTemplateId = 101;
        item.ExpirationTime = DateTime.UtcNow.AddDays(1);
        item.ExpirationOnlineMinutesLeft = 29.5;
        var expiration = item.ExpirationTime;
        Bid(_other, lot, 600);
        ClearPackets();
        var prepared = false;
        _checkpoint = () =>
        {
            prepared = _buyer.Money == 8000 && _auctions.AuctionLots.IsEmpty &&
                item.OwnerId == _buyer.Id && item.SlotType == SlotType.Mail &&
                _mails._allPlayerMails.Count == 3 && _sessions.Values.All(session => session.Packets.Count == 0);
            return true;
        };
        Bid(_buyer, lot, int.MaxValue);
        await Assert.That(prepared).IsTrue();
        await Assert.That(_buyer.Money).IsEqualTo(8000L);
        await Assert.That(Mail(MailType.AucOffSuccess).Body.CopperCoins).IsEqualTo(1800);
        await Assert.That(Mail(MailType.AucBidFail).Body.CopperCoins).IsEqualTo(600);
        await Assert.That(Mail(MailType.AucBidWin).Body.Attachments.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Detail).IsEquivalentTo(new byte[] { 8, 6, 7, 5, 3, 0, 9 });
        await Assert.That(item.UccId).IsEqualTo(55UL);
        await Assert.That(item.Grade).IsEqualTo((byte)6);
        await Assert.That(item.ImageItemTemplateId).IsEqualTo(101U);
        await Assert.That(item.ExpirationTime).IsEqualTo(expiration);
        await Assert.That(item.ExpirationOnlineMinutesLeft).IsEqualTo(29.5);
        Bid(_buyer, lot, 2000);
        _auctions.CancelAuctionLot(_seller, lot.Id);
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(3);
        await Assert.That(_buyer.Money).IsEqualTo(8000L);
        _auctionIds.ReleaseId((uint)lot.Id).WasCalled(Times.Never);
    }

    [Test]
    [Arguments("insufficient")]
    [Arguments("own")]
    [Arguments("expired")]
    [Arguments("low")]
    [Arguments("mismatch")]
    public async Task InvalidBid_CannotChangeWalletListingOrMails(string invalid)
    {
        var lot = Listed();
        var player = invalid == "own" ? _seller : _buyer;
        if (invalid == "insufficient") player.Money = 99;
        if (invalid == "expired") lot.EndTime = DateTime.UtcNow.AddSeconds(-1);
        var money = player.Money;
        var bid = new AuctionBid { LotId = invalid == "mismatch" ? lot.Id + 1 : lot.Id, Money = invalid == "low" ? 99 : 500 };
        _auctions.BidOnAuctionLot(player, 0, 0, new AuctionLot { Id = lot.Id }, bid);
        await Assert.That(player.Money).IsEqualTo(money);
        await Assert.That(lot.BidderId).IsEqualTo(0U);
        await Assert.That(_mails._allPlayerMails).IsEmpty();
        await Assert.That(_commits).IsEqualTo(1);
    }

    [Test]
    public async Task Cancel_RequiresSellerAndUnbidListingThenReturnsOriginalItemOnce()
    {
        var lot = Listed();
        _auctions.CancelAuctionLot(_other, lot.Id);
        await Assert.That(_auctions.AuctionLots.ContainsKey(lot.Id)).IsTrue();
        _auctions.CancelAuctionLot(_seller, lot.Id);
        _auctions.CancelAuctionLot(_seller, lot.Id);
        var returned = Mail(MailType.AucOffCancel);
        await Assert.That(returned.Body.Attachments.Single()).IsSameReferenceAs(lot.Item);
        await Assert.That(lot.Item.OwnerId).IsEqualTo((ulong)_seller.Id);
        await Assert.That(_commits).IsEqualTo(2);
        var bidLot = Listed(2);
        Bid(_buyer, bidLot, 500);
        _auctions.CancelAuctionLot(_seller, bidLot.Id);
        await Assert.That(_auctions.AuctionLots.ContainsKey(bidLot.Id)).IsTrue();
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Expiry_SettlesBidOrReturnsUnbidExactItemOnce(bool withBid)
    {
        var lot = Listed();
        if (withBid) Bid(_buyer, lot, 500);
        lot.EndTime = DateTime.UtcNow.AddSeconds(-1);
        _auctions.UpdateAuctionHouse();
        _auctions.UpdateAuctionHouse();
        await Assert.That(_auctions.AuctionLots).IsEmpty();
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(withBid ? 2 : 1);
        await Assert.That(Mail(withBid ? MailType.AucBidWin : MailType.AucOffFail).Body.Attachments.Single())
            .IsSameReferenceAs(lot.Item);
        if (withBid)
            await Assert.That(Mail(MailType.AucOffSuccess).Body.CopperCoins).IsEqualTo(450);
    }

    [Test]
    [Arguments("bid")]
    [Arguments("buyout")]
    [Arguments("cancel")]
    [Arguments("expiry")]
    public async Task KnownCheckpointFailure_RestoresAllParticipantsAndAllowsSingleRetry(string operation)
    {
        var lot = Listed();
        if (operation is "bid" or "buyout") Bid(_other, lot, 400);
        if (operation == "expiry") lot.EndTime = DateTime.UtcNow.AddSeconds(-1);
        lot.IsDirty = false;
        lot.Item.IsDirty = false;
        ClearPackets();
        _checkpoint = () => false;
        Act(operation, lot);
        await Assert.That(_auctions.AuctionLots[lot.Id]).IsSameReferenceAs(lot);
        await Assert.That(lot.Item._holdingContainer).IsSameReferenceAs(_seller.Inventory.AuctionAttachments);
        await Assert.That(lot.Item.IsDirty).IsFalse();
        await Assert.That(lot.IsDirty).IsFalse();
        await Assert.That(lot.BidMoney).IsEqualTo(operation is "bid" or "buyout" ? 400 : 0);
        await Assert.That(_buyer.Money).IsEqualTo(10000L);
        await Assert.That(_mails._allPlayerMails).IsEmpty();
        await Assert.That(DeletedLots()).IsEmpty();
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count(packet => Opcode(packet) is
            SCOffsets.SCAuctionBidPacket or SCOffsets.SCAuctionCanceledPacket or SCOffsets.SCGotMailPacket))).IsEqualTo(0);
        _checkpoint = () => true;
        Act(operation, lot);
        await Assert.That(operation == "bid" ? lot.BidMoney == 500 : !_auctions.AuctionLots.ContainsKey(lot.Id)).IsTrue();
    }

    [Test]
    public async Task UnknownBuyoutCommit_PreservesPreparedStateWithoutSuccessNotifications()
    {
        var lot = Listed();
        ClearPackets();
        _checkpoint = () => throw new InvalidOperationException("uncertain commit");
        Assert.Throws<InvalidOperationException>(() => Bid(_buyer, lot, 2000));
        await Assert.That(_buyer.Money).IsEqualTo(8000L);
        await Assert.That(_auctions.AuctionLots).IsEmpty();
        await Assert.That(Mail(MailType.AucBidWin).Body.Attachments.Single()).IsSameReferenceAs(lot.Item);
        await Assert.That(_mails._allPlayerMails.Values.All(mail => !mail.IsDelivered)).IsTrue();
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count)).IsEqualTo(0);
        _auctionIds.ReleaseId((uint)lot.Id).WasCalled(Times.Never);
        _mailIds.ReleaseId(Any<uint>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task CommittedNotificationException_DoesNotRestoreListingOrRemoveSettlementMails()
    {
        var lot = Listed();
        _sessions[_buyer.Id].OnPacket = () => throw new InvalidOperationException("connection failed");
        Assert.Throws<InvalidOperationException>(() => Bid(_buyer, lot, 2000));
        await Assert.That(_buyer.Money).IsEqualTo(8000L);
        await Assert.That(_auctions.AuctionLots).IsEmpty();
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(2);
        await Assert.That(Mail(MailType.AucBidWin).Body.Attachments.Single()).IsSameReferenceAs(lot.Item);
    }

    [Test]
    [Arguments("buyout")]
    [Arguments("bid")]
    [Arguments("cancel")]
    public async Task ConcurrentBuyoutBidOrCancel_SettlesOneLotOnce(string competingAction)
    {
        var lot = Listed();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var competing = new ManualResetEventSlim();
        _checkpoint = () => { entered.Set(); return release.Wait(TimeSpan.FromSeconds(10)); };
        var first = Task.Run(() => Bid(_buyer, lot, 2000));
        var checkpointReached = entered.Wait(TimeSpan.FromSeconds(10));
        var second = Task.Run(() =>
        {
            competing.Set();
            if (competingAction == "cancel") _auctions.CancelAuctionLot(_seller, lot.Id);
            else Bid(_other, lot, competingAction == "buyout" ? 2000 : 500);
        });
        competing.Wait(TimeSpan.FromSeconds(10));
        release.Set();
        await Task.WhenAll(first, second);
        await Assert.That(checkpointReached).IsTrue();
        await Assert.That(_buyer.Money + _other.Money).IsEqualTo(18000L);
        await Assert.That(_mails._allPlayerMails.Count).IsEqualTo(2);
        await Assert.That(_commits).IsEqualTo(2);
        await Assert.That(Mail(MailType.AucBidWin).Body.Attachments.Single()).IsSameReferenceAs(lot.Item);
    }

    private void Act(string operation, AuctionLot lot)
    {
        if (operation == "cancel") _auctions.CancelAuctionLot(_seller, lot.Id);
        else if (operation == "expiry") _auctions.UpdateAuctionHouse();
        else Bid(_buyer, lot, operation == "buyout" ? 2000 : 500);
    }

    private CharacterMock Character(uint id, string name)
    {
        var session = new RecordingSession();
        _sessions[id] = session;
        var player = new CharacterMock { Id = id, Name = name, Money = 10000, NumInventorySlots = 20,
            NumBankSlots = 20, Connection = new GameConnection(session) };
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate) continue;
            var container = new ItemContainer(id, type, false, player)
                { Owner = player, ContainerId = (ulong)_containers.Count + 1 };
            _containers.Add(container.ContainerId, container);
        }
        player.Inventory = new Inventory(player);
        player.Mails = new CharacterMails(player);
        return player;
    }

    private Item Item(ulong id = 1)
    {
        var template = new ItemTemplate { Id = 100, MaxCount = 100, BindType = ItemBindType.Normal };
        _templates[100] = template;
        var item = new Item { Id = id, TemplateId = 100, Template = template, OwnerId = _seller.Id,
            SlotType = SlotType.Inventory, Slot = (int)id + 2, Count = 3, _holdingContainer = _seller.Inventory.Bag };
        _allItems[id] = item;
        _seller.Inventory.Bag.Items.Add(item);
        _seller.Inventory.Bag.UpdateFreeSlotCount();
        return item;
    }

    private void Post(Item item) => _auctions.PostLotOnAuction(_seller, 0, 0, item.Id, 100, 2000, AuctionDuration.AuctionDuration6Hours);
    private AuctionLot Listed(ulong id = 1)
    {
        var item = Item(id);
        Post(item);
        return _auctions.AuctionLots.Values.Single(lot => lot.Item == item);
    }

    private void Bid(Character player, AuctionLot lot, int amount) => _auctions.BidOnAuctionLot(player, 0, 0,
        new AuctionLot { Id = lot.Id, ClientId = 999, StartMoney = 1, DirectMoney = 1 },
        new AuctionBid { LotId = lot.Id, BidderId = 999, BidderName = "Spoof", Money = amount });
    private BaseMail Mail(MailType type) => _mails._allPlayerMails.Values.Single(mail => mail.MailType == type);
    private void ClearPackets() { foreach (var session in _sessions.Values) session.Packets.Clear(); }
    private int Packets(Character player, ushort opcode) => _sessions[player.Id].Packets.Count(packet => Opcode(packet) == opcode);
    private static ushort Opcode(byte[] packet) => BitConverter.ToUInt16(packet, 6);
    private ConcurrentBag<long> DeletedLots() => (ConcurrentBag<long>)typeof(AuctionManager)
        .GetField("<DeletedAuctionItemIds>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_auctions)!;
    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public Action OnPacket { get; set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { Packets.Add(packet.ToArray()); OnPacket?.Invoke(); }
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
