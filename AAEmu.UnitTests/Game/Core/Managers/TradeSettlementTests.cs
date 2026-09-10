using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using ShutdownTask = AAEmu.Game.Models.Tasks.ShutdownTask;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class TradeSettlementTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private readonly Dictionary<uint, RecordingSession> _sessions = [];
    private Dictionary<ulong, Item> _items;
    private Dictionary<ulong, ItemContainer> _containers;
    private Dictionary<uint, ItemTemplate> _templates;
    private List<ulong> _deleted;
    private WorldManager _worldManager;
    private WorldInstance _world;
    private TradeManager _trade;
    private RecordingSave _save;
    private Ids _tradeIds;
    private CharacterMock _owner;
    private CharacterMock _target;
    private CharacterMock _third;

    [Before(Test)]
    public void SetUp()
    {
        _worldManager = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetInstance(_worldManager);
        _world = CreateWorld(1);
        var itemManager = new ItemManager(Mock.Of<ISkillManager>().Object, new Ids(),
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, _worldManager);
        SetInstance(itemManager);
        SetInstance(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _items = [];
        _containers = [];
        _templates = [];
        _deleted = [];
        SetField(itemManager, "_allItems", _items);
        SetField(itemManager, "_removedItems", _deleted);
        SetField(itemManager, "_allPersistentContainers", _containers);
        SetField(itemManager, "_templates", _templates);
        _owner = CreateCharacter(1);
        _target = CreateCharacter(2);
        _third = CreateCharacter(3);
        _save = new RecordingSave();
        _tradeIds = new Ids();
        _trade = new TradeManager(_tradeIds, _worldManager, itemManager, _save);
        SetInstance(_trade);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var session in _sessions.Values)
            session.OnPacket = null;
        foreach (var character in _worldManager.GetAllCharacters())
            _trade.CancelTrade(character, 0);
        _trade.CancelTrade(_owner, 0);
        _trade.CancelTrade(_target, 0);
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
    }

    [Test]
    public async Task RunningSkillEffect_RejectsTradeUntilEffectExecutionFinishes()
    {
        var skills = new SkillManager(null, null);
        SetField(skills, "_skillReagents", new Dictionary<uint, SkillReagent>());
        SetField(skills, "_skillProducts", new Dictionary<uint, SkillProduct>());
        SetInstance(skills);
        var wasBusy = false;
        var startedDuringEffect = false;
        var skill = new Skill(new SkillTemplate { Id = 50, TargetType = SkillTargetType.Self });
        skill.Template.Effects.Add(new SkillEffect
        {
            EndLevel = byte.MaxValue, Chance = 100, ApplicationMethod = SkillEffectApplicationMethod.Source,
            Template = new TradeAdmissionEffect(() =>
            {
                wasBusy = Skill.IsExecuting(_owner) && !Monitor.IsEntered(SaveManager.PersistenceSyncRoot);
                _trade.CanStartTrade(_owner, _target);
                _trade.StartTrade(_owner, _target);
                startedDuringEffect = PacketCount(_owner, SCOffsets.SCTradeStartedPacket) != 0;
            })
        });

        skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId), _owner, new SkillCastUnitTarget(_owner.ObjId), null);

        await Assert.That(wasBusy).IsTrue();
        await Assert.That(startedDuringEffect).IsFalse();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
        _trade.CanStartTrade(_owner, _target);
        _trade.StartTrade(_owner, _target);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeStartedPacket)).IsEqualTo(1);
    }

    private sealed class TradeAdmissionEffect(Action action) : EffectTemplate
    {
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null) => action();
    }

    [Test]
    public async Task Acceptance_RequiresMatchingInvitationAndRejectsReplays()
    {
        _trade.StartTrade(_owner, _target);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeStartedPacket)).IsEqualTo(0);
        _trade.CanStartTrade(_owner, _target);
        _trade.StartTrade(_third, _target);
        _trade.StartTrade(_target, _owner);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeStartedPacket)).IsEqualTo(0);
        _trade.StartTrade(_owner, _target);
        _trade.StartTrade(_owner, _target);
        _trade.CanStartTrade(_third, _target);
        _trade.StartTrade(_third, _target);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeStartedPacket)).IsEqualTo(1);
        await Assert.That(PacketCount(_target, SCOffsets.SCTradeStartedPacket)).IsEqualTo(1);
        await Assert.That(PacketCount(_third, SCOffsets.SCTradeStartedPacket)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Decline_OnlyMatchingInvitedCharacterCanReleasePendingTrade()
    {
        _trade.CanStartTrade(_owner, _target);
        _trade.DeclineTrade(_third, _owner.ObjId, 0);
        _trade.DeclineTrade(_target, _third.ObjId, 0);
        _trade.CanStartTrade(_third, _target);
        _trade.StartTrade(_third, _target);
        await Assert.That(PacketCount(_target, SCOffsets.SCTradeStartedPacket)).IsEqualTo(0);
        _trade.DeclineTrade(_target, _owner.ObjId, 0);
        _trade.CanStartTrade(_third, _target);
        _trade.StartTrade(_third, _target);
        await Assert.That(PacketCount(_target, SCOffsets.SCTradeStartedPacket)).IsEqualTo(1);
    }

    [Test]
    [Arguments("dead")]
    [Arguments("offline")]
    [Arguments("combat")]
    [Arguments("duel")]
    [Arguments("crafting")]
    [Arguments("distance")]
    [Arguments("height")]
    [Arguments("nan")]
    [Arguments("instance")]
    [Arguments("world")]
    [Arguments("faction")]
    [Arguments("inactive")]
    [Arguments("unregistered")]
    [Arguments("self")]
    public async Task Invitation_RejectsIneligibleParticipants(string invalid)
    {
        switch (invalid)
        {
            case "dead": _target.Hp = 0; break;
            case "offline": SetOnline(_target, false); break;
            case "combat": _target.IsInBattle = true; break;
            case "duel": _target.IsInDuel = true; break;
            case "crafting": _target.Craft = new CharacterCraft(_target) { IsCrafting = true }; break;
            case "distance": _target.Transform.Local.SetPosition(5.01f, 0, 0); break;
            case "height": _target.Transform.Local.SetPosition(0, 0, 5.01f); break;
            case "nan": _target.Transform.Local.SetPosition(float.NaN, 0, 0); break;
            case "instance": _target.ParentWorld = CreateWorld(2); break;
            case "world":
                typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
                    .GetField("<WorldId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(_target.Transform, 2U);
                break;
            case "faction": _target.Faction = new SystemFaction { Id = FactionsEnum.Hostile }; break;
            case "inactive": _target.Connection.ActiveChar = null; break;
            case "unregistered": _worldManager.TryRemoveCharacter(_target.ObjId); break;
            case "self": _target = _owner; break;
        }
        Begin();
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeStartedPacket)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Distance_AtFiveMetersAllowsTradeButMovingAwayBeforeConfirmationCancels()
    {
        _target.Transform.Local.SetPosition(3, 4, 0);
        Begin();
        _trade.AddMoney(_owner, 25);
        LockBoth();
        _trade.OkTrade(_owner);
        _target.Transform.Local.SetPosition(3, 4.1f, 0);
        _trade.OkTrade(_target);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeStartedPacket)).IsEqualTo(1);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeCanceledPacket)).IsEqualTo(1);
        await Assert.That(TradeReservation.GetReservedMoney(_owner)).IsEqualTo(0);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(6)]
    [Arguments(int.MaxValue)]
    public async Task InvalidItemAmount_CancelsWithoutChangingAssets(int amount)
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, amount);
        ConfirmBoth();
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(item.OwnerId).IsEqualTo((ulong)_owner.Id);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("soulbound")]
    [Arguments("wrongowner")]
    [Arguments("unregistered")]
    [Arguments("duplicateslot")]
    [Arguments("duplicatesameoffer")]
    [Arguments("bank")]
    public async Task InvalidOrDuplicateItemOffer_CancelsWithoutTransferring(string invalid)
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        switch (invalid)
        {
            case "soulbound": item.ItemFlags |= ItemFlag.SoulBound; break;
            case "wrongowner": item.OwnerId = _target.Id; break;
            case "unregistered": _items.Remove(item.Id); break;
            case "duplicateslot": _owner.Inventory.Bag.Items.Add(item); break;
            case "duplicatesameoffer": _trade.AddItem(_owner, SlotType.Inventory, 0, 2); break;
        }
        _trade.AddItem(_owner, invalid == "bank" ? SlotType.Bank : SlotType.Inventory, 0, 2);
        ConfirmBoth();
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(_target.Inventory.Bag.Items).IsEmpty();
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task RemovingUnrelatedSlot_DoesNotClearTheRemainingOffer()
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.RemoveItem(_owner, SlotType.Inventory, 9);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(2);
        ConfirmBoth();
        await Assert.That(item.Count).IsEqualTo(3);
        await Assert.That(_target.Inventory.Bag.Items.Single().Count).IsEqualTo(2);
        await Assert.That(_save.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task PartialOffer_DisplaysAndTransfersExactAmountWithSeparateItemIdentity()
    {
        var item = AddItem(_owner, 10, 100, 5);
        item.Grade = 3;
        item.CreateTime = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        var offer = Body(_target, SCOffsets.SCOtherTradeItemPutupPacket);
        await Assert.That(offer.ReadUInt32()).IsEqualTo(100U);
        await Assert.That(offer.ReadUInt64()).IsEqualTo(item.Id);
        await Assert.That(offer.ReadByte()).IsEqualTo((byte)3);
        offer.ReadByte();
        await Assert.That(offer.ReadInt32()).IsEqualTo(2);
        _save.BeforeReturn = () =>
        {
            if (_target.Inventory.Bag.Items.Single().Count != 2 || item.Count != 3 ||
                PacketCount(_owner, SCOffsets.SCTradeMadePacket) != 0)
                throw new InvalidOperationException("Settlement must be staged and unpublished at checkpoint.");
        };
        ConfirmBoth();
        var received = _target.Inventory.Bag.Items.Single();
        await Assert.That(received.Id == item.Id).IsFalse();
        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received.Grade).IsEqualTo(item.Grade);
        await Assert.That(received.CreateTime).IsEqualTo(item.CreateTime);
        await Assert.That(received.OwnerId).IsEqualTo((ulong)_target.Id);
        await Assert.That(_items[received.Id]).IsSameReferenceAs(received);
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(3);
        await Assert.That(_save.Participants.Length).IsEqualTo(2);
        await Assert.That(_save.Participants.Any(participant => ReferenceEquals(participant, _owner))).IsTrue();
        await Assert.That(_save.Participants.Any(participant => ReferenceEquals(participant, _target))).IsTrue();
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeMadePacket)).IsEqualTo(1);
        await Assert.That(PacketCount(_target, SCOffsets.SCTradeMadePacket)).IsEqualTo(1);
    }

    [Test]
    public async Task ConsumingUnreservedRemainder_KeepsTheExactOfferedQuantityValid()
    {
        var item = AddItem(_owner, 10, 100, 7);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 3);
        var consumed = _owner.Inventory.Bag.ConsumeItem(ItemTaskType.Invalid, item.TemplateId, 2, item);
        await Assert.That(consumed).IsEqualTo(2);
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(3);
        ConfirmBoth();
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(_target.Inventory.Bag.Items.Single().Count).IsEqualTo(3);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeCanceledPacket)).IsEqualTo(0);
    }

    [Test]
    public async Task BothFullBags_ExchangeOutgoingItemsBeforeAddingIncomingItems()
    {
        var first = AddItem(_owner, 10, 100, 1);
        var second = AddItem(_target, 20, 200, 1);
        _owner.Inventory.Bag.ContainerSize = _target.Inventory.Bag.ContainerSize = 1;
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 1);
        _trade.AddItem(_target, SlotType.Inventory, 0, 1);
        ConfirmBoth();
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(second);
        await Assert.That(_target.Inventory.Bag.Items.Single()).IsSameReferenceAs(first);
        var body = Body(_owner, SCOffsets.SCTradeMadePacket);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)ItemTaskType.Trade);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)2);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)ItemAction.Seize);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)SlotType.Inventory);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)0);
        await Assert.That(body.ReadUInt64()).IsEqualTo(first.Id);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)ItemAction.Create);
        await Assert.That(_save.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task FullRecipientBag_FailsBeforeCheckpointAndRestoresEveryAsset()
    {
        var first = AddItem(_owner, 10, 100, 1);
        var second = AddItem(_target, 20, 200, 1);
        _target.Inventory.Bag.ContainerSize = 1;
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 1);
        _trade.AddMoney(_target, 50);
        ConfirmBoth();
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(first);
        await Assert.That(_target.Inventory.Bag.Items.Single()).IsSameReferenceAs(second);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_target.Money).IsEqualTo(100L);
        await Assert.That(TradeReservation.GetReservedMoney(_target)).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedCount(first)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeMadePacket)).IsEqualTo(0);
    }

    [Test]
    public async Task BothLocksRequired_UnlockAndOfferChangesClearBothConfirmations()
    {
        Begin();
        _trade.AddMoney(_owner, 20);
        _trade.LockTrade(_owner, true);
        _trade.OkTrade(_owner);
        _trade.OkTrade(_target);
        await Assert.That(_save.Calls).IsEqualTo(0);
        _trade.LockTrade(_target, true);
        _trade.OkTrade(_owner);
        _trade.LockTrade(_target, false);
        _trade.OkTrade(_target);
        await Assert.That(_save.Calls).IsEqualTo(0);
        LockBoth();
        _trade.OkTrade(_owner);
        _trade.AddMoney(_owner, 25);
        LockBoth();
        _trade.OkTrade(_target);
        await Assert.That(_save.Calls).IsEqualTo(0);
        _trade.OkTrade(_owner);
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(75L);
        await Assert.That(_target.Money).IsEqualTo(125L);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(101)]
    [Arguments(int.MaxValue)]
    public async Task InvalidMoneyOffer_CancelsAndReleasesPreviousMoney(int amount)
    {
        Begin();
        _trade.AddMoney(_owner, 50);
        _trade.AddMoney(_owner, amount);
        ConfirmBoth();
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_target.Money).IsEqualTo(100L);
        await Assert.That(TradeReservation.GetReservedMoney(_owner)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ZeroMoneyOffer_ReleasesEarlierReservationWithoutDebit()
    {
        Begin();
        _trade.AddMoney(_owner, 50);
        _trade.AddMoney(_owner, 0);
        await Assert.That(TradeReservation.GetReservedMoney(_owner)).IsEqualTo(0);
        _trade.AddMoney(_target, 20);
        ConfirmBoth();
        await Assert.That(_owner.Money).IsEqualTo(120L);
        await Assert.That(_target.Money).IsEqualTo(80L);
    }

    [Test]
    public async Task RecipientMoneyOverflow_RestoresBothWalletsAndOutgoingItem()
    {
        var item = AddItem(_target, 20, 200, 1);
        _target.Money = long.MaxValue;
        Begin();
        _trade.AddMoney(_owner, 1);
        _trade.AddItem(_target, SlotType.Inventory, 0, 1);
        ConfirmBoth();
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_target.Money).IsEqualTo(long.MaxValue);
        await Assert.That(_target.Inventory.Bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task CompletionPacketObserver_ReentrantConfirmationSeesRetiredTrade()
    {
        Begin();
        _trade.AddMoney(_owner, 25);
        _sessions[_owner.ObjId].OnPacket = packet =>
        {
            if (BitConverter.ToUInt16(packet, 6) != SCOffsets.SCTradeMadePacket)
                return;
            _trade.OkTrade(_owner);
            _trade.OkTrade(_target);
        };
        ConfirmBoth();
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(75L);
        await Assert.That(_target.Money).IsEqualTo(125L);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeMadePacket)).IsEqualTo(1);
        await Assert.That(PacketCount(_target, SCOffsets.SCTradeMadePacket)).IsEqualTo(1);
    }

    [Test]
    [Arguments("grade")]
    [Arguments("count")]
    [Arguments("registration")]
    [Arguments("slot")]
    [Arguments("money")]
    public async Task ChangedOfferBeforeFinalConfirmation_CancelsWithoutCheckpoint(string changed)
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.AddMoney(_owner, 50);
        LockBoth();
        _trade.OkTrade(_owner);
        switch (changed)
        {
            case "grade": item.Grade++; break;
            case "count": item.Count = 1; break;
            case "registration": _items.Remove(item.Id); break;
            case "slot": item.Slot = 1; break;
            case "money": _owner.Money = 49; break;
        }
        _trade.OkTrade(_target);
        await Assert.That(_save.Calls).IsEqualTo(0);
        await Assert.That(_target.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_target.Money).IsEqualTo(100L);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedMoney(_owner)).IsEqualTo(0);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeCanceledPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task KnownCheckpointFailure_RestoresAssetsAndAllowsExactlyOneFreshRetry()
    {
        var item = AddItem(_owner, 10, 100, 5);
        item.IsDirty = false;
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.AddMoney(_target, 25);
        _save.Result = false;
        ConfirmBoth();
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(item.IsDirty).IsFalse();
        await Assert.That(_target.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_target.Money).IsEqualTo(100L);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeMadePacket)).IsEqualTo(0);
        _trade.OkTrade(_target);
        await Assert.That(_save.Calls).IsEqualTo(1);
        _save.Result = true;
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.AddMoney(_target, 25);
        ConfirmBoth();
        _trade.OkTrade(_owner);
        _trade.OkTrade(_target);
        await Assert.That(_save.Calls).IsEqualTo(2);
        await Assert.That(item.Count).IsEqualTo(3);
        await Assert.That(_target.Inventory.Bag.Items.Single().Count).IsEqualTo(2);
        await Assert.That(_owner.Money).IsEqualTo(125L);
        await Assert.That(_target.Money).IsEqualTo(75L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CheckpointOrPostCommitNotificationException_NeverRefundsOrReplays(bool afterCommit)
    {
        var item = AddItem(_owner, 10, 100, 1);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 1);
        _trade.AddMoney(_target, 25);
        LockBoth();
        _trade.OkTrade(_owner);
        if (afterCommit)
            _sessions[_owner.ObjId].OnPacket = packet =>
            {
                if (BitConverter.ToUInt16(packet, 6) == SCOffsets.SCTradeMadePacket)
                    throw new IOException("Post-commit notification failure");
            };
        else
            _save.BeforeReturn = () => throw new IOException("Unknown commit outcome");
        Exception thrown = null;
        try { _trade.OkTrade(_target); }
        catch (Exception exception) { thrown = exception; }
        _sessions[_owner.ObjId].OnPacket = null;
        _trade.OkTrade(_target);
        _trade.CancelTrade(_owner, 0);
        await Assert.That(thrown).IsTypeOf<IOException>();
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_target.Inventory.Bag.Items.Single()).IsSameReferenceAs(item);
        await Assert.That(_owner.Money).IsEqualTo(125L);
        await Assert.That(_target.Money).IsEqualTo(75L);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedMoney(_target)).IsEqualTo(0);
        await Assert.That(_tradeIds.Released.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentFinalConfirmations_CommitOnceAndConserveMoney()
    {
        Begin();
        _trade.AddMoney(_owner, 50);
        _trade.AddMoney(_target, 20);
        LockBoth();
        _trade.OkTrade(_owner);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait();
            _trade.OkTrade(_target);
        })).ToArray();
        start.Set();
        await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(70L);
        await Assert.That(_target.Money).IsEqualTo(130L);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeMadePacket)).IsEqualTo(1);
        await Assert.That(_tradeIds.Released.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExpiryInvalidation_CancelsEntireOfferAndReleasesBothPlayersReservations()
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.AddMoney(_target, 50);
        lock (SaveManager.PersistenceSyncRoot)
            TradeReservation.Invalidate(item);
        ConfirmBoth();
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedMoney(_target)).IsEqualTo(0);
        await Assert.That(_save.Calls).IsEqualTo(0);
        await Assert.That(PacketCount(_owner, SCOffsets.SCTradeCanceledPacket)).IsEqualTo(1);
        await Assert.That(item.Count).IsEqualTo(5);
    }

    [Test]
    public async Task CancellationAndReconnect_StaleCharacterCannotCancelReplacementTrade()
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.AddMoney(_target, 50);
        _trade.CancelTrade(_owner, 0);
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedMoney(_target)).IsEqualTo(0);
        var stale = _owner;
        _worldManager.TryRemoveCharacter(stale.ObjId);
        _owner = CreateCharacter(1, initializeInventory: false);
        _owner.Inventory = stale.Inventory;
        Begin();
        _trade.AddMoney(_owner, 20);
        _trade.CancelTrade(stale, 0);
        ConfirmBoth();
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(80L);
        await Assert.That(_target.Money).IsEqualTo(120L);
    }

    [Test]
    public async Task CancellationPacketFailure_ReleasesOffersAndStillNotifiesOtherParticipant()
    {
        var item = AddItem(_owner, 10, 100, 5);
        Begin();
        _trade.AddItem(_owner, SlotType.Inventory, 0, 2);
        _trade.AddMoney(_target, 25);
        _sessions[_owner.ObjId].OnPacket = _ => throw new IOException("Disconnected session");
        _trade.CancelTrade(_owner, 0);
        _sessions[_owner.ObjId].OnPacket = null;
        await Assert.That(TradeReservation.GetReservedCount(item)).IsEqualTo(0);
        await Assert.That(TradeReservation.GetReservedMoney(_target)).IsEqualTo(0);
        await Assert.That(PacketCount(_target, SCOffsets.SCTradeCanceledPacket)).IsEqualTo(1);
        Begin();
        _trade.AddMoney(_owner, 20);
        ConfirmBoth();
        await Assert.That(_save.Calls).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(80L);
        await Assert.That(_target.Money).IsEqualTo(120L);
    }

    [Test]
    public async Task TradePackets_WithoutActiveCharacterIgnoreWellFormedRequests()
    {
        (GamePacket Packet, PacketStream Body)[] requests =
        [
            (new CSCanStartTradePacket(), new PacketStream().WriteBc(1U)),
            (new CSStartTradePacket(), new PacketStream().WriteBc(1U)),
            (new CSCannotStartTradePacket(), new PacketStream().WriteBc(1U).Write(0)),
            (new CSCancelTradePacket(), new PacketStream().Write(0)),
            (new CSPutupTradeItemPacket(), new PacketStream().Write((byte)1).Write((byte)0).Write(1)),
            (new CSPutupTradeMoneyPacket(), new PacketStream().Write(1)),
            (new CSTakedownTradeItemPacket(), new PacketStream().Write((byte)1).Write((byte)0)),
            (new CSTradeLockPacket(), new PacketStream().Write(true)),
            (new CSTradeOkPacket(), new PacketStream())
        ];
        foreach (var (packet, body) in requests)
        {
            packet.Read(new PacketStream(body.GetBytes()));
            packet.Connection = new GameConnection(new RecordingSession());
            packet.Read(new PacketStream(body.GetBytes()));
        }
        await Assert.That(_save.Calls).IsEqualTo(0);
    }

    private void Begin()
    {
        _trade.CanStartTrade(_owner, _target);
        _trade.StartTrade(_owner, _target);
    }

    private void LockBoth()
    {
        _trade.LockTrade(_owner, true);
        _trade.LockTrade(_target, true);
    }

    private void ConfirmBoth()
    {
        LockBoth();
        _trade.OkTrade(_owner);
        _trade.OkTrade(_target);
    }

    private int PacketCount(Character character, ushort opcode) =>
        _sessions[character.ObjId].Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == opcode);

    private PacketStream Body(Character character, ushort opcode) => new(
        _sessions[character.ObjId].Packets.Last(packet => BitConverter.ToUInt16(packet, 6) == opcode)[8..]);

    private CharacterMock CreateCharacter(uint id, bool initializeInventory = true)
    {
        var session = new RecordingSession();
        _sessions[id] = session;
        var character = new CharacterMock
        {
            Id = id, ObjId = id, Name = $"Trader{id}", Money = 100, Hp = 100,
            NumInventorySlots = 10, NumBankSlots = 10, ParentWorld = _world,
            Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance, MotherId = FactionsEnum.NuiaAlliance },
            Connection = new GameConnection(session)
        };
        character.Connection.ActiveChar = character;
        SetOnline(character, true);
        _worldManager.TryAddCharacter(character);
        if (initializeInventory)
        {
            foreach (var slotType in Enum.GetValues<SlotType>())
            {
                if (slotType == SlotType.EquipmentMate)
                    continue;
                var container = new ItemContainer(id, slotType, false, character)
                    { ContainerId = (ulong)_containers.Count + 1, Owner = character };
                _containers.Add(container.ContainerId, container);
            }
            character.Inventory = new Inventory(character);
        }
        return character;
    }

    private Item AddItem(Character owner, uint id, uint templateId, int count)
    {
        if (!_templates.TryGetValue(templateId, out var template))
            _templates.Add(templateId, template = new ItemTemplate
                { Id = templateId, MaxCount = 100, BindType = ItemBindType.Normal });
        var bag = owner.Inventory.Bag;
        var item = new ItemMock(id, template, count)
        {
            OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = bag.Items.Count,
            _holdingContainer = bag
        };
        bag.Items.Add(item);
        bag.UpdateFreeSlotCount();
        _items.Add(id, item);
        return item;
    }

    private WorldInstance CreateWorld(uint id)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, id);
        ((ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worldManager)!)
            .AddOrUpdate(id, world, (_, _) => world);
        return world;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetOnline(Character character, bool value) => typeof(Character)
        .GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(character, value);

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class Ids : IItemIdManager, ITradeIdManager
    {
        private uint _next = 10000;
        public List<uint> Released { get; } = [];
        public bool Initialize(bool forceReset = false) => true;
        public void Load() { }
        public uint GetNextId() => _next++;
        public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => GetNextId()).ToArray();
        public void ReleaseId(uint usedObjectId) => Released.Add(usedObjectId);
        public void ReleaseId(IEnumerable<uint> usedObjectIds) => Released.AddRange(usedObjectIds);
    }

    private sealed class RecordingSave : ISaveManager
    {
        public ShutdownTask ShutdownTask { get; set; }
        public bool Result { get; set; } = true;
        public int Calls { get; private set; }
        public Action BeforeReturn { get; set; }
        public Character[] Participants { get; private set; } = [];
        public void Initialize() { }
        public Task StopAsync() => Task.CompletedTask;
        public void SaveTickStart() { }
        public bool DoSave() => true;
        public bool TryCommitEconomy(IReadOnlyCollection<Character> participants,
            Action<PersistenceSaveContext> writeSettlement = null)
        {
            if (!Monitor.IsEntered(SaveManager.PersistenceSyncRoot))
                throw new InvalidOperationException("Trade checkpoint requires the shared persistence gate.");
            Calls++;
            Participants = [.. participants];
            BeforeReturn?.Invoke();
            return Result;
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public Action<byte[]> OnPacket { get; set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { Packets.Add(packet.ToArray()); OnPacket?.Invoke(packet); }
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
