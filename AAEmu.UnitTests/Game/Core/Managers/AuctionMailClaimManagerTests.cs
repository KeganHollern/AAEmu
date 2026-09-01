using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class AuctionMailClaimManagerTests
{
    private const uint AuctionBuyAchievementId = 1000;
    private const uint AuctionSoldAchievementId = 1001;
    private static readonly DateTimeOffset ClaimTime =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly FieldInfo s_achievementsField =
        typeof(Character).GetField(
            "<Achievements>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_inventoryBagField =
        typeof(Inventory).GetField(
            "<Bag>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly object _accountSyncRoot = new();
    private AchievementGameData _achievementData;
    private UnitRequirementsGameData _unitRequirementsData;
    private MailManager _mailManager;
    private ServiceProvider _serviceProvider;

    [Before(Test)]
    public void Setup()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.AuctionBuy);
        data.AddAchievement(AuctionBuyAchievementId, 100, false);
        data.AddObjective(1, AuctionBuyAchievementId, 100);
        data.AddRecord(101, CharRecordKind.AuctionSold);
        data.AddAchievement(AuctionSoldAchievementId, 100, false);
        data.AddObjective(2, AuctionSoldAchievementId, 101);
        _achievementData = data.Build();
        _unitRequirementsData = data.BuildUnitRequirements();

        ResetSingleton<MailManager>();
        ResetSingleton<QuestManager>();

        _mailManager = new MailManager(
            Mock.Of<IMailIdManager>().Object,
            Mock.Of<INameManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object)
        {
            _allPlayerMails = new ConcurrentDictionary<long, BaseMail>()
        };
        var questManager = new QuestManager(
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IZoneManager>().Object);

        var services = new ServiceCollection();
        services.AddSingleton(_mailManager);
        services.AddSingleton(questManager);
        _serviceProvider = services.BuildServiceProvider();
        SingletonContainer.ServiceProvider = _serviceProvider;
    }

    [After(Test)]
    public void Teardown()
    {
        _mailManager._allPlayerMails = null;
        SingletonContainer.ServiceProvider = null;
        ResetSingleton<MailManager>();
        ResetSingleton<QuestManager>();
        _serviceProvider.Dispose();
    }

    [Test]
    public async Task GetAttached_BuyMail_CommitsItemMailAndProgressBeforePublishing()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var forgottenItemIds = new List<ulong>();
        var retainedItemIds = new List<ulong>();
        var manager = CreateManager(store, forgottenItemIds, retainedItemIds);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, item) = AddBuyMail(character, 20, 5);
        var sawAtomicState = false;
        store.OnPersisting = (plan, achievements) =>
        {
            sawAtomicState = plan is AuctionBuyClaimPlan &&
                achievements.GetAmount(AuctionBuyAchievementId) == 1 &&
                character.Inventory.Bag.Items.Count == 0 &&
                mail.Body.Attachments.Count == 1 &&
                session.Packets.Count == 0;
        };

        var claimed = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(claimed).IsTrue();
        await Assert.That(sawAtomicState).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(store.FindReceipt(mail.Id, character.Id)?.ClaimType)
            .IsEqualTo(AuctionMailClaimType.BuyItem);
        await Assert.That(character.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(character.Inventory.Bag.Items[0]).IsSameReferenceAs(item);
        await Assert.That(item._holdingContainer).IsSameReferenceAs(character.Inventory.Bag);
        await Assert.That(mail.Body.Attachments).IsEmpty();
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(mail.IsDirty).IsFalse();
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(1u);
        await Assert.That(session.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
        await Assert.That(session.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAchievementsPacket))).IsTrue();
        await Assert.That(forgottenItemIds).IsEmpty();
        await Assert.That(retainedItemIds).IsEquivalentTo([item.Id]);
    }

    [Test]
    public async Task GetAttached_BuyCoinAttachment_DoesNotTreatMailSourceAsDestinationStack()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var forgottenItemIds = new List<ulong>();
        var retainedItemIds = new List<ulong>();
        var manager = CreateManager(store, forgottenItemIds, retainedItemIds);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session, money: 0);
        var (mail, item) = AddBuyMail(character, Item.Coins, 5);
        var plannedAsNewStack = false;
        store.OnPersisting = (plan, _) =>
        {
            plannedAsNewStack = plan is AuctionBuyClaimPlan { DestinationStack: null };
        };

        var claimed = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(claimed).IsTrue();
        await Assert.That(plannedAsNewStack).IsTrue();
        await Assert.That(character.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(character.Inventory.Bag.Items[0]).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(mail.Body.Attachments).IsEmpty();
        await Assert.That(forgottenItemIds).IsEmpty();
        await Assert.That(retainedItemIds).IsEquivalentTo([item.Id]);
    }

    [Test]
    public async Task GetAttached_SaleMail_CommitsMoneyMailCharacterAndProgressBeforePublishing()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var manager = CreateManager(store);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var mail = AddSaleMail(character, 250);
        var sawAtomicState = false;
        store.OnPersisting = (plan, achievements) =>
        {
            sawAtomicState = plan is AuctionSaleClaimPlan &&
                achievements.GetAmount(AuctionSoldAchievementId) == 1 &&
                character.Money == 1_000 &&
                character.LaborPower == 10 &&
                mail.Body.CopperCoins == 250 &&
                session.Packets.Count == 0;
        };

        var claimed = mails.GetAttached(mail.Id, true, false, true);

        await Assert.That(claimed).IsTrue();
        await Assert.That(sawAtomicState).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(store.FindReceipt(mail.Id, character.Id)?.ClaimType)
            .IsEqualTo(AuctionMailClaimType.SaleMoney);
        await Assert.That(character.Money).IsEqualTo(1_250);
        await Assert.That(character.LaborPower).IsEqualTo((short)9);
        await Assert.That(character.ConsumedLaborPower).IsEqualTo(1);
        await Assert.That(character.Actability.Actabilities[(uint)ActabilityType.Commerce].Point)
            .IsEqualTo(1);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(0);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(mail.IsDirty).IsFalse();
        await Assert.That(character.Achievements.GetAmount(AuctionSoldAchievementId)).IsEqualTo(1u);
        await Assert.That(session.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
        await Assert.That(session.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAchievementsPacket))).IsTrue();
    }

    [Test]
    public async Task GetAttached_SameProcessRetry_ReplaysBuyReceiptWithoutDuplicateGrantOrProgress()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var retainedItemIds = new List<ulong>();
        var manager = CreateManager(store, retainedItemIds: retainedItemIds);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, item) = AddBuyMail(character, 20, 5);

        var firstClaim = mails.GetAttached(mail.Id, false, true, true);
        session.Packets.Clear();
        var retry = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(firstClaim).IsTrue();
        await Assert.That(retry).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(character.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(character.Inventory.Bag.Items[0]).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(1u);
        await Assert.That(retainedItemIds).IsEquivalentTo([item.Id]);
        await Assert.That(session.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
    }

    [Test]
    public async Task GetAttached_ConcurrentRetry_ReplaysCommittedReceipt()
    {
        using var persistStarted = new ManualResetEventSlim();
        using var allowCommit = new ManualResetEventSlim();
        using var retryStarted = new ManualResetEventSlim();
        var store = new InMemoryAuctionMailClaimStore
        {
            OnPersisting = (_, _) =>
            {
                persistStarted.Set();
                if (!allowCommit.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting to release the fake commit.");
            }
        };
        var manager = CreateManager(store);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, item) = AddBuyMail(character, 20, 5);

        var firstClaim = Task.Run(() => mails.GetAttached(mail.Id, false, true, true));
        await Assert.That(persistStarted.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        var retry = Task.Run(() =>
        {
            retryStarted.Set();
            return mails.GetAttached(mail.Id, false, true, true);
        });
        await Assert.That(retryStarted.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        await Task.Delay(25);
        allowCommit.Set();

        var results = await Task.WhenAll(firstClaim, retry);

        await Assert.That(results[0]).IsTrue();
        await Assert.That(results[1]).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(character.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(character.Inventory.Bag.Items[0]).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(1u);
    }

    [Test]
    public async Task GetAttached_BuyStackMerge_ForgetsSourceOnceAndRetryChangesNothing()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var forgottenItemIds = new List<ulong>();
        var retainedItemIds = new List<ulong>();
        var manager = CreateManager(store, forgottenItemIds, retainedItemIds);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, sourceItem) = AddBuyMail(character, 20, 5);
        var bag = character.Inventory.Bag;
        var destinationStack = new Item(30_002, sourceItem.Template, 7)
        {
            OwnerId = character.Id,
            SlotType = SlotType.Inventory,
            Slot = 0,
            _holdingContainer = bag,
            IsDirty = false
        };
        bag.Items.Add(destinationStack);
        bag.UpdateFreeSlotCount();
        var sawDurableMergePlan = false;
        store.OnPersisting = (plan, achievements) =>
        {
            sawDurableMergePlan = plan is AuctionBuyClaimPlan buyPlan &&
                ReferenceEquals(buyPlan.SourceItem, sourceItem) &&
                ReferenceEquals(buyPlan.DestinationStack, destinationStack) &&
                buyPlan.DestinationCountBefore == 7 &&
                buyPlan.DestinationCountAfter == 12 &&
                buyPlan.SourceItem.Id == sourceItem.Id &&
                achievements.GetAmount(AuctionBuyAchievementId) == 1;
        };

        var firstClaim = mails.GetAttached(mail.Id, false, true, true);
        var retry = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(firstClaim).IsTrue();
        await Assert.That(retry).IsTrue();
        await Assert.That(sawDurableMergePlan).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(bag.Items).HasSingleItem();
        await Assert.That(bag.Items[0]).IsSameReferenceAs(destinationStack);
        await Assert.That(destinationStack.Count).IsEqualTo(12);
        await Assert.That(forgottenItemIds).IsEquivalentTo([sourceItem.Id]);
        await Assert.That(retainedItemIds).IsEquivalentTo([sourceItem.Id]);
        await Assert.That(mail.Body.Attachments).IsEmpty();
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(1u);
    }

    [Test]
    public async Task GetAttached_ExistingBuyReceiptWithStaleLoadedMail_StopsForConsistency()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var forgottenItemIds = new List<ulong>();
        var retainedItemIds = new List<ulong>();
        var consistencyFailures = new List<(string Message, Exception Exception)>();
        var manager = CreateManager(
            store,
            forgottenItemIds,
            retainedItemIds,
            (message, exception) => consistencyFailures.Add((message, exception)));
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, item) = AddBuyMail(character, 20, 5);
        store.SeedReceipt(new AuctionMailClaimReceipt(
            mail.Id,
            AuctionMailClaimType.BuyItem,
            character.Id,
            item.Id,
            item.Count,
            SlotType.Inventory,
            0,
            null));

        var claimed = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(claimed).IsFalse();
        await Assert.That(store.PersistCalls).IsEqualTo(0);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(consistencyFailures).HasSingleItem();
        await Assert.That(consistencyFailures[0].Exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(character.Inventory.Bag.Items).IsEmpty();
        await Assert.That(mail.Body.Attachments).HasSingleItem();
        await Assert.That(mail.Body.Attachments[0]).IsSameReferenceAs(item);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)1);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(0u);
        await Assert.That(forgottenItemIds).IsEmpty();
        await Assert.That(retainedItemIds).IsEmpty();
        await Assert.That(session.Packets).IsEmpty();
    }

    [Test]
    public async Task GetAttached_ExistingSaleReceiptWithStaleLoadedMail_StopsForConsistency()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var consistencyFailures = new List<(string Message, Exception Exception)>();
        var manager = CreateManager(
            store,
            stopForConsistencyFailure:
                (message, exception) => consistencyFailures.Add((message, exception)));
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var mail = AddSaleMail(character, 250);
        store.SeedReceipt(new AuctionMailClaimReceipt(
            mail.Id,
            AuctionMailClaimType.SaleMoney,
            character.Id,
            null,
            null,
            null,
            null,
            mail.Body.CopperCoins));

        var claimed = mails.GetAttached(mail.Id, true, false, true);

        await Assert.That(claimed).IsFalse();
        await Assert.That(store.PersistCalls).IsEqualTo(0);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(consistencyFailures).HasSingleItem();
        await Assert.That(consistencyFailures[0].Exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(character.Money).IsEqualTo(1_000);
        await Assert.That(character.LaborPower).IsEqualTo((short)10);
        await Assert.That(character.ConsumedLaborPower).IsEqualTo(0);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(250);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)1);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(character.Achievements.GetAmount(AuctionSoldAchievementId)).IsEqualTo(0u);
        await Assert.That(session.Packets).IsEmpty();
    }

    [Test]
    public async Task GetAttached_BuyDefinitiveCommitFailure_CanRetryWithoutDuplicateGrantOrProgress()
    {
        var store = new InMemoryAuctionMailClaimStore { FailCommitDefinitively = true };
        var forgottenItemIds = new List<ulong>();
        var retainedItemIds = new List<ulong>();
        var manager = CreateManager(store, forgottenItemIds, retainedItemIds);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, item) = AddBuyMail(character, 20, 5);

        var claimed = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(claimed).IsFalse();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(store.CommitAttempts).IsEqualTo(1);
        await Assert.That(store.ReceiptCount).IsEqualTo(0);
        await Assert.That(character.Inventory.Bag.Items).IsEmpty();
        await Assert.That(mail.Body.Attachments).HasSingleItem();
        await Assert.That(mail.Body.Attachments[0]).IsSameReferenceAs(item);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)1);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(0u);
        await Assert.That(forgottenItemIds).IsEmpty();
        await Assert.That(retainedItemIds).IsEmpty();
        await Assert.That(session.Packets).IsEmpty();

        store.FailCommitDefinitively = false;
        var retry = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(retry).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(2);
        await Assert.That(store.CommitAttempts).IsEqualTo(2);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(store.GetDurableClaim(mail.Id, AuctionMailClaimType.BuyItem).AuctionBuyProgress)
            .IsEqualTo(1u);
        await Assert.That(character.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(character.Inventory.Bag.Items[0]).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(5);
        await Assert.That(mail.Body.Attachments).IsEmpty();
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(1u);
        await Assert.That(forgottenItemIds).IsEmpty();
        await Assert.That(retainedItemIds).IsEquivalentTo([item.Id]);
    }

    [Test]
    public async Task GetAttached_SaleDefinitiveCommitFailure_LeavesDurableAndLiveStateUnchanged()
    {
        var store = new InMemoryAuctionMailClaimStore { FailCommitDefinitively = true };
        var manager = CreateManager(store);
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var mail = AddSaleMail(character, 250);

        var claimed = mails.GetAttached(mail.Id, true, false, true);

        await Assert.That(claimed).IsFalse();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(store.CommitAttempts).IsEqualTo(1);
        await Assert.That(store.ReceiptCount).IsEqualTo(0);
        await Assert.That(character.Money).IsEqualTo(1_000);
        await Assert.That(character.LaborPower).IsEqualTo((short)10);
        await Assert.That(character.ConsumedLaborPower).IsEqualTo(0);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(250);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)1);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(character.Achievements.GetAmount(AuctionSoldAchievementId)).IsEqualTo(0u);
        await Assert.That(session.Packets).IsEmpty();
    }

    [Test]
    public async Task GetAttached_SaleAfterRestart_RehydratesDurableStateAndReplaysLoadedMail()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var firstManager = CreateManager(store);
        var firstSession = new RecordingSession();
        var (firstCharacter, firstMails) = CreateCharacter(firstManager, firstSession);
        var mail = AddSaleMail(firstCharacter, 250);
        var firstClaim = firstMails.GetAttached(mail.Id, true, false, true);

        var restartedStore = store.Restart();
        var restartedManager = CreateManager(restartedStore);
        var restartedSession = new RecordingSession();
        var (restartedCharacter, restartedMails, reloadedMail) = RehydrateDurableClaim(
            restartedStore,
            mail.Id,
            AuctionMailClaimType.SaleMoney,
            restartedManager,
            restartedSession);

        var replay = restartedMails.GetAttached(mail.Id, true, false, true);

        await Assert.That(firstClaim).IsTrue();
        await Assert.That(replay).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(restartedStore.PersistCalls).IsEqualTo(0);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(reloadedMail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(reloadedMail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(reloadedMail.Body.CopperCoins).IsEqualTo(0);
        await Assert.That(restartedCharacter.Money).IsEqualTo(1_250);
        await Assert.That(restartedCharacter.LaborPower).IsEqualTo((short)9);
        await Assert.That(restartedCharacter.ConsumedLaborPower).IsEqualTo(1);
        await Assert.That(
                restartedCharacter.Actability.Actabilities[(uint)ActabilityType.Commerce].Point)
            .IsEqualTo(1);
        await Assert.That(restartedCharacter.Achievements.GetAmount(AuctionSoldAchievementId))
            .IsEqualTo(1u);
        await Assert.That(restartedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
        await Assert.That(restartedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAchievementsPacket))).IsTrue();
    }

    [Test]
    public async Task GetAttached_BuyAfterRestart_RehydratesDurableItemAndReplaysLoadedMail()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var retainedItemIds = new List<ulong>();
        var firstManager = CreateManager(store, retainedItemIds: retainedItemIds);
        var firstSession = new RecordingSession();
        var (firstCharacter, firstMails) = CreateCharacter(firstManager, firstSession);
        var (mail, claimedItem) = AddBuyMail(firstCharacter, 20, 5);
        var firstClaim = firstMails.GetAttached(mail.Id, false, true, true);

        var restartedStore = store.Restart();
        var restartedForgottenItemIds = new List<ulong>();
        var restartedRetainedItemIds = new List<ulong>();
        var restartedManager = CreateManager(
            restartedStore,
            restartedForgottenItemIds,
            restartedRetainedItemIds);
        var restartedSession = new RecordingSession();
        var (restartedCharacter, restartedMails, reloadedMail) = RehydrateDurableClaim(
            restartedStore,
            mail.Id,
            AuctionMailClaimType.BuyItem,
            restartedManager,
            restartedSession);

        var replay = restartedMails.GetAttached(mail.Id, false, true, true);

        await Assert.That(firstClaim).IsTrue();
        await Assert.That(replay).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(restartedStore.PersistCalls).IsEqualTo(0);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(reloadedMail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(reloadedMail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(reloadedMail.Body.Attachments).IsEmpty();
        await Assert.That(restartedCharacter.Inventory.Bag.Items).HasSingleItem();
        var restartedItem = restartedCharacter.Inventory.Bag.Items[0];
        await Assert.That(restartedItem.Id).IsEqualTo(claimedItem.Id);
        await Assert.That(restartedItem.Count).IsEqualTo(5);
        await Assert.That(restartedItem.SlotType).IsEqualTo(SlotType.Inventory);
        await Assert.That(restartedItem.Slot).IsEqualTo(claimedItem.Slot);
        await Assert.That(restartedItem._holdingContainer)
            .IsSameReferenceAs(restartedCharacter.Inventory.Bag);
        await Assert.That(restartedItem.ItemFlags).IsEqualTo(claimedItem.ItemFlags);
        await Assert.That(restartedCharacter.Achievements.GetAmount(AuctionBuyAchievementId))
            .IsEqualTo(1u);
        await Assert.That(retainedItemIds).IsEquivalentTo([claimedItem.Id]);
        await Assert.That(restartedForgottenItemIds).IsEmpty();
        await Assert.That(restartedRetainedItemIds).IsEmpty();
        await Assert.That(restartedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
        await Assert.That(restartedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAchievementsPacket))).IsTrue();
    }

    [Test]
    public async Task GetAttached_BuyAfterRestartWithMailAbsent_ReplaysReceiptWithoutDuplicateGrant()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var retainedItemIds = new List<ulong>();
        var firstManager = CreateManager(store, retainedItemIds: retainedItemIds);
        var firstSession = new RecordingSession();
        var (firstCharacter, firstMails) = CreateCharacter(firstManager, firstSession);
        var (mail, claimedItem) = AddBuyMail(firstCharacter, 20, 5);
        var firstClaim = firstMails.GetAttached(mail.Id, false, true, true);

        var restartedStore = store.Restart();
        var restartedForgottenItemIds = new List<ulong>();
        var restartedRetainedItemIds = new List<ulong>();
        var restartedManager = CreateManager(
            restartedStore,
            restartedForgottenItemIds,
            restartedRetainedItemIds);
        var restartedSession = new RecordingSession();
        var (restartedCharacter, restartedMails, _) = RehydrateDurableClaim(
            restartedStore,
            mail.Id,
            AuctionMailClaimType.BuyItem,
            restartedManager,
            restartedSession);
        var removedReloadedMail = _mailManager._allPlayerMails.TryRemove(mail.Id, out _);

        var replay = restartedMails.GetAttached(mail.Id, false, true, true);

        await Assert.That(firstClaim).IsTrue();
        await Assert.That(removedReloadedMail).IsTrue();
        await Assert.That(replay).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(restartedStore.PersistCalls).IsEqualTo(0);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(restartedCharacter.Inventory.Bag.Items).HasSingleItem();
        await Assert.That(restartedCharacter.Inventory.Bag.Items[0].Id).IsEqualTo(claimedItem.Id);
        await Assert.That(restartedCharacter.Inventory.Bag.Items[0].Count).IsEqualTo(5);
        await Assert.That(restartedCharacter.Achievements.GetAmount(AuctionBuyAchievementId))
            .IsEqualTo(1u);
        await Assert.That(retainedItemIds).IsEquivalentTo([claimedItem.Id]);
        await Assert.That(restartedForgottenItemIds).IsEmpty();
        await Assert.That(restartedRetainedItemIds).IsEmpty();
        await Assert.That(restartedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
        await Assert.That(restartedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAchievementsPacket))).IsTrue();
    }

    [Test]
    public async Task GetAttached_PersistReplay_StopsForConsistencyWithoutPublishingOrMutatingLiveState()
    {
        var store = new InMemoryAuctionMailClaimStore { ReplayOnNextPersist = true };
        var forgottenItemIds = new List<ulong>();
        var retainedItemIds = new List<ulong>();
        var consistencyFailures = new List<(string Message, Exception Exception)>();
        var manager = CreateManager(
            store,
            forgottenItemIds,
            retainedItemIds,
            (message, exception) => consistencyFailures.Add((message, exception)));
        var session = new RecordingSession();
        var (character, mails) = CreateCharacter(manager, session);
        var (mail, item) = AddBuyMail(character, 20, 5);

        var claimed = mails.GetAttached(mail.Id, false, true, true);

        await Assert.That(claimed).IsFalse();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(consistencyFailures).HasSingleItem();
        await Assert.That(consistencyFailures[0].Message)
            .Contains("committed by another Game instance");
        await Assert.That(consistencyFailures[0].Exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(character.Inventory.Bag.Items).IsEmpty();
        await Assert.That(mail.Body.Attachments).HasSingleItem();
        await Assert.That(mail.Body.Attachments[0]).IsSameReferenceAs(item);
        await Assert.That(character.Achievements.GetAmount(AuctionBuyAchievementId)).IsEqualTo(0u);
        await Assert.That(forgottenItemIds).IsEmpty();
        await Assert.That(retainedItemIds).IsEmpty();
        await Assert.That(session.Packets).IsEmpty();
    }

    [Test]
    public async Task GetAttached_PostCommitPacketFailure_FreshConnectionRehydratesAndReplays()
    {
        var store = new InMemoryAuctionMailClaimStore();
        var manager = CreateManager(store);
        var session = new RecordingSession { ThrowOnSend = true };
        var (character, mails) = CreateCharacter(manager, session);
        var mail = AddSaleMail(character, 250);

        var committedWithPacketFailure = mails.GetAttached(mail.Id, true, false, true);

        await Assert.That(committedWithPacketFailure).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(character.Money).IsEqualTo(1_250);
        await Assert.That(character.Achievements.GetAmount(AuctionSoldAchievementId)).IsEqualTo(1u);
        await Assert.That(session.SendAttempts).IsEqualTo(1);
        await Assert.That(session.Packets).IsEmpty();
        await Assert.That(session.Closed).IsTrue();

        var reconnectedStore = store.Restart();
        var reconnectedManager = CreateManager(reconnectedStore);
        var reconnectedSession = new RecordingSession();
        var (reconnectedCharacter, reconnectedMails, _) = RehydrateDurableClaim(
            reconnectedStore,
            mail.Id,
            AuctionMailClaimType.SaleMoney,
            reconnectedManager,
            reconnectedSession);
        var replay = reconnectedMails.GetAttached(mail.Id, true, false, true);

        await Assert.That(replay).IsTrue();
        await Assert.That(store.PersistCalls).IsEqualTo(1);
        await Assert.That(reconnectedStore.PersistCalls).IsEqualTo(0);
        await Assert.That(store.ReceiptCount).IsEqualTo(1);
        await Assert.That(reconnectedCharacter.Money).IsEqualTo(1_250);
        await Assert.That(reconnectedCharacter.Achievements.GetAmount(AuctionSoldAchievementId))
            .IsEqualTo(1u);
        await Assert.That(reconnectedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket))).IsTrue();
        await Assert.That(reconnectedSession.Packets.Any(packet =>
            HasOpcode(packet, SCOffsets.SCAchievementsPacket))).IsTrue();
    }

    private AuctionMailClaimManager CreateManager(
        InMemoryAuctionMailClaimStore store,
        List<ulong> forgottenItemIds = null,
        List<ulong> retainedItemIds = null,
        Action<string, Exception> stopForConsistencyFailure = null)
    {
        return new AuctionMailClaimManager(
            store,
            new FakeTimeProvider(ClaimTime),
            _ => _accountSyncRoot,
            CreateSalePlan,
            (forgottenItemIds ?? []).Add,
            (retainedItemIds ?? []).Add,
            stopForConsistencyFailure);
    }

    private (CharacterMock Character, CharacterMails Mails) CreateCharacter(
        AuctionMailClaimManager manager,
        RecordingSession session,
        long money = 1_000,
        short labor = 10,
        int consumedLabor = 0,
        int commercePoint = 0,
        uint auctionBuyProgress = 0,
        uint auctionSoldProgress = 0)
    {
        var character = new CharacterMock
        {
            Id = 7,
            ObjId = 70,
            AccountId = 3,
            Name = "auction-tester",
            Money = money,
            Level = 1,
            ConsumedLaborPower = consumedLabor,
            NumInventorySlots = 10,
            NumBankSlots = 10
        };
        character.InitializeLaborCache(labor, ClaimTime.UtcDateTime);
        character.Actability = new CharacterActability(character);
        character.Actability.Actabilities[(uint)ActabilityType.Commerce] = new Actability(
            new ActabilityTemplate { Id = (uint)ActabilityType.Commerce })
        {
            Point = commercePoint,
            Step = 0
        };
        character.Abilities = new CharacterAbilities(character);
        character.Inventory = CreateInventory(character);

        var achievements = new CharacterAchievements(
            character,
            _achievementData,
            new FakeTimeProvider(ClaimTime),
            () => true,
            unitRequirementsData: _unitRequirementsData);
        s_achievementsField.SetValue(character, achievements);
        if (auctionBuyProgress > 0)
            achievements.Increment(CharRecordKind.AuctionBuy, 0, 0, auctionBuyProgress);
        if (auctionSoldProgress > 0)
            achievements.Increment(CharRecordKind.AuctionSold, 0, 0, auctionSoldProgress);

        character.Connection = new GameConnection(session);
        character.Connection.ActiveChar = character;

        var mails = new CharacterMails(character, manager);
        character.Mails = mails;
        return (character, mails);
    }

    private (CharacterMock Character, CharacterMails Mails, BaseMail Mail) RehydrateDurableClaim(
        InMemoryAuctionMailClaimStore store,
        long mailId,
        AuctionMailClaimType claimType,
        AuctionMailClaimManager manager,
        RecordingSession session)
    {
        var durable = store.GetDurableClaim(mailId, claimType);
        var (character, mails) = CreateCharacter(
            manager,
            session,
            durable.Money,
            durable.Labor,
            durable.ConsumedLabor,
            durable.ActabilityPoint,
            durable.AuctionBuyProgress,
            durable.AuctionSoldProgress);
        character.ApplyCommittedAuctionSaleState(
            durable.Money,
            durable.Labor,
            durable.Experience,
            durable.Level,
            durable.ConsumedLabor,
            durable.UpdatedAt);
        foreach (var (abilityId, experience) in durable.AbilityExperience)
            character.Abilities.Abilities[abilityId].Exp = experience;

        if (durable.DeliveredItem != null)
        {
            var item = RestoreItem(durable.DeliveredItem, character.Inventory.Bag);
            character.Inventory.Bag.Items.Add(item);
            character.Inventory.Bag.UpdateFreeSlotCount();
        }

        var mail = new BaseMail
        {
            Id = durable.Receipt.MailId,
            MailType = durable.MailType,
            ReceiverName = durable.ReceiverName,
            OpenDate = durable.MailOpenDate,
            Header =
            {
                Status = durable.MailStatus,
                Attachments = durable.MailAttachmentCount,
                SenderId = 0,
                ReceiverId = durable.Receipt.ReceiverId
            },
            Body =
            {
                CopperCoins = durable.MailCopperCoins,
                RecvDate = ClaimTime.UtcDateTime
            }
        };
        mail.IsDirty = false;
        _mailManager._allPlayerMails = new ConcurrentDictionary<long, BaseMail>(
            new[] { new KeyValuePair<long, BaseMail>(mail.Id, mail) });
        return (character, mails, mail);
    }

    private static Item RestoreItem(DurableItemSnapshot durable, ItemContainer bag)
    {
        return new Item(
            durable.Id,
            new ItemTemplate
            {
                Id = durable.TemplateId,
                Name = durable.TemplateName,
                MaxCount = durable.MaxCount,
                BindType = durable.BindType,
                FixedGrade = durable.FixedGrade
            },
            durable.Count)
        {
            OwnerId = bag.OwnerId,
            SlotType = SlotType.Inventory,
            Slot = durable.Slot,
            Grade = durable.Grade,
            ItemFlags = durable.Flags,
            _holdingContainer = bag,
            IsDirty = false
        };
    }

    private BaseMail AddSaleMail(Character character, int copperCoins)
    {
        var mail = new BaseMail
        {
            Id = 20_001,
            MailType = MailType.AucOffSuccess,
            ReceiverName = character.Name,
            OpenDate = DateTime.UnixEpoch,
            Header =
            {
                Status = MailStatus.Unread,
                Attachments = 1,
                SenderId = 0,
                ReceiverId = character.Id
            },
            Body =
            {
                CopperCoins = copperCoins,
                RecvDate = ClaimTime.UtcDateTime
            }
        };
        mail.IsDirty = false;
        _mailManager._allPlayerMails[mail.Id] = mail;
        character.Mails.UnreadMailCount.UpdateReceived(mail.MailType, 1);
        return mail;
    }

    private (BaseMail Mail, Item Item) AddBuyMail(
        Character character,
        uint templateId,
        int count)
    {
        var mailContainer = new ItemContainer(character.Id, SlotType.Mail, false, character)
        {
            Owner = character,
            ContainerSize = MailBody.MaxMailAttachments,
            IsDirty = false
        };
        var item = new Item(
            30_001,
            new ItemTemplate
            {
                Id = templateId,
                Name = $"Auction item {templateId}",
                MaxCount = 100,
                BindType = ItemBindType.Normal,
                FixedGrade = 0
            },
            count)
        {
            OwnerId = character.Id,
            SlotType = SlotType.Mail,
            Slot = 0,
            _holdingContainer = mailContainer,
            IsDirty = false
        };
        mailContainer.Items.Add(item);
        mailContainer.UpdateFreeSlotCount();

        var mail = new BaseMail
        {
            Id = 20_002,
            MailType = MailType.AucBidWin,
            ReceiverName = character.Name,
            OpenDate = DateTime.UnixEpoch,
            Header =
            {
                Status = MailStatus.Unread,
                Attachments = 1,
                SenderId = 0,
                ReceiverId = character.Id
            },
            Body =
            {
                RecvDate = ClaimTime.UtcDateTime
            }
        };
        mail.Body.Attachments.Add(item);
        mail.IsDirty = false;
        _mailManager._allPlayerMails[mail.Id] = mail;
        character.Mails.UnreadMailCount.UpdateReceived(mail.MailType, 1);
        return (mail, item);
    }

    private static Inventory CreateInventory(Character character)
    {
        var bag = new ItemContainer(character.Id, SlotType.Inventory, false, character)
        {
            Owner = character,
            ContainerSize = character.NumInventorySlots,
            IsDirty = false
        };
        var inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        s_inventoryBagField.SetValue(inventory, bag);
        return inventory;
    }

    private static AuctionSaleClaimPlan CreateSalePlan(
        Character character,
        BaseMail mail,
        DateTime now)
    {
        var moneyAfter = checked(character.Money + mail.Body.CopperCoins);
        var receipt = new AuctionMailClaimReceipt(
            mail.Id,
            AuctionMailClaimType.SaleMoney,
            character.Id,
            null,
            null,
            null,
            null,
            mail.Body.CopperCoins);
        var commerce = character.Actability.Actabilities[(uint)ActabilityType.Commerce];
        return new AuctionSaleClaimPlan(
            character,
            mail,
            receipt,
            MailStatus.Read,
            now,
            checked((byte)(mail.Header.Attachments - 1)),
            mail.Body.Attachments.ToArray(),
            now,
            character.Money,
            moneyAfter,
            character.LaborPower,
            checked((short)(character.LaborPower - 1)),
            character.Experience,
            character.Experience,
            0,
            character.Level,
            character.Level,
            checked(character.ConsumedLaborPower + 1),
            (uint)ActabilityType.Commerce,
            commerce.Point,
            checked(commerce.Point + 1),
            1,
            commerce.Step,
            character.Abilities.Abilities.ToDictionary(pair => pair.Key, pair => pair.Value.Exp),
            character.Abilities.Abilities.ToDictionary(pair => pair.Key, _ => character.Level));
    }

    private static bool HasOpcode(byte[] packet, ushort opcode)
    {
        return packet.Length >= 8 &&
            packet[6] == (byte)opcode &&
            packet[7] == (byte)(opcode >> 8);
    }

    private static void ResetSingleton<T>() where T : class
    {
        typeof(Singleton<T>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, null);
    }

    private sealed record DurableItemSnapshot(
        ulong Id,
        uint TemplateId,
        string TemplateName,
        int MaxCount,
        ItemBindType BindType,
        int FixedGrade,
        byte Grade,
        int Count,
        int Slot,
        ItemFlag Flags);

    private sealed record DurableClaimSnapshot(
        AuctionMailClaimReceipt Receipt,
        MailType MailType,
        string ReceiverName,
        MailStatus MailStatus,
        DateTime MailOpenDate,
        byte MailAttachmentCount,
        int MailCopperCoins,
        long Money,
        short Labor,
        int Experience,
        byte Level,
        int ConsumedLabor,
        DateTime UpdatedAt,
        uint ActabilityId,
        int ActabilityPoint,
        byte ActabilityStep,
        IReadOnlyDictionary<AbilityType, int> AbilityExperience,
        uint AuctionBuyProgress,
        uint AuctionSoldProgress,
        DurableItemSnapshot DeliveredItem,
        ulong? DeletedSourceItemId);

    private sealed class InMemoryAuctionClaimDatabase
    {
        public object SyncRoot { get; } = new();
        public Dictionary<(long MailId, AuctionMailClaimType ClaimType), AuctionMailClaimReceipt> Receipts { get; } = [];
        public Dictionary<(long MailId, AuctionMailClaimType ClaimType), DurableClaimSnapshot> Claims { get; } = [];
    }

    private sealed class InMemoryAuctionMailClaimStore : IAuctionMailClaimStore
    {
        private readonly InMemoryAuctionClaimDatabase _database;

        public InMemoryAuctionMailClaimStore()
            : this(new InMemoryAuctionClaimDatabase())
        {
        }

        private InMemoryAuctionMailClaimStore(InMemoryAuctionClaimDatabase database)
        {
            _database = database;
        }

        public int PersistCalls { get; private set; }
        public int CommitAttempts { get; private set; }
        public int ReceiptCount
        {
            get
            {
                lock (_database.SyncRoot)
                    return _database.Receipts.Count;
            }
        }
        public bool FailCommitDefinitively { get; set; }
        public bool ReplayOnNextPersist { get; set; }
        public Action<AuctionMailClaimPlan, CharacterAchievements> OnPersisting { get; set; }

        public InMemoryAuctionMailClaimStore Restart()
        {
            return new InMemoryAuctionMailClaimStore(_database);
        }

        public DurableClaimSnapshot GetDurableClaim(long mailId, AuctionMailClaimType claimType)
        {
            lock (_database.SyncRoot)
                return _database.Claims[(mailId, claimType)];
        }

        public void SeedReceipt(AuctionMailClaimReceipt receipt)
        {
            lock (_database.SyncRoot)
                _database.Receipts.Add((receipt.MailId, receipt.ClaimType), receipt);
        }

        public AuctionMailClaimReceipt FindReceipt(long mailId, uint receiverId)
        {
            lock (_database.SyncRoot)
            {
                return _database.Receipts.Values
                    .FirstOrDefault(receipt =>
                        receipt.MailId == mailId && receipt.ReceiverId == receiverId);
            }
        }

        public AuctionMailClaimPersistenceResult Persist(
            AuctionMailClaimPlan plan,
            CharacterAchievements achievements)
        {
            PersistCalls++;
            OnPersisting?.Invoke(plan, achievements);
            CommitAttempts++;
            if (FailCommitDefinitively)
                throw new InvalidOperationException("Injected definitive commit failure; no rows were written.");

            var key = (plan.Receipt.MailId, plan.Receipt.ClaimType);
            lock (_database.SyncRoot)
            {
                if (_database.Receipts.ContainsKey(key))
                    return AuctionMailClaimPersistenceResult.Replay;

                var durable = CaptureDurableClaim(plan, achievements);
                _database.Claims.Add(key, durable);
                _database.Receipts.Add(key, plan.Receipt);
                if (ReplayOnNextPersist)
                {
                    ReplayOnNextPersist = false;
                    return AuctionMailClaimPersistenceResult.Replay;
                }

                return AuctionMailClaimPersistenceResult.Created;
            }
        }

        private static DurableClaimSnapshot CaptureDurableClaim(
            AuctionMailClaimPlan plan,
            CharacterAchievements achievements)
        {
            var character = plan.Character;
            var salePlan = plan as AuctionSaleClaimPlan;
            var buyPlan = plan as AuctionBuyClaimPlan;
            var actabilityId = salePlan?.ActabilityId ?? (uint)ActabilityType.Commerce;
            var actability = character.Actability.Actabilities[actabilityId];
            var abilityExperience = salePlan?.AbilityExperienceAfter.ToDictionary(pair => pair.Key, pair => pair.Value) ??
                character.Abilities.Abilities.ToDictionary(pair => pair.Key, pair => pair.Value.Exp);
            DurableItemSnapshot deliveredItem = null;
            if (buyPlan != null)
            {
                var item = buyPlan.DestinationStack ?? buyPlan.SourceItem;
                deliveredItem = new DurableItemSnapshot(
                    item.Id,
                    item.TemplateId,
                    item.Template.Name,
                    item.Template.MaxCount,
                    item.Template.BindType,
                    item.Template.FixedGrade,
                    item.Grade,
                    buyPlan.DestinationCountAfter,
                    buyPlan.DestinationSlot,
                    buyPlan.FinalItemFlags);
            }

            return new DurableClaimSnapshot(
                plan.Receipt,
                plan.Mail.MailType,
                plan.Mail.ReceiverName,
                plan.FinalMailStatus,
                plan.FinalOpenDate,
                plan.FinalAttachmentCount,
                salePlan == null ? plan.Mail.Body.CopperCoins : 0,
                salePlan?.MoneyAfter ?? character.Money,
                salePlan?.LaborAfter ?? character.LaborPower,
                salePlan?.ExperienceAfter ?? character.Experience,
                salePlan?.LevelAfter ?? character.Level,
                salePlan?.ConsumedLaborAfter ?? character.ConsumedLaborPower,
                plan.UpdatedAt,
                actabilityId,
                salePlan?.ActabilityPointAfter ?? actability.Point,
                salePlan?.ActabilityStep ?? actability.Step,
                abilityExperience,
                achievements.GetAmount(AuctionBuyAchievementId),
                achievements.GetAmount(AuctionSoldAchievementId),
                deliveredItem,
                buyPlan?.DestinationStack == null ? null : buyPlan.SourceItem.Id);
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        private readonly object _packetSyncRoot = new();

        public List<byte[]> Packets { get; } = [];
        public int SendAttempts { get; private set; }
        public bool ThrowOnSend { get; set; }
        public bool Closed { get; private set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            lock (_packetSyncRoot)
            {
                SendAttempts++;
                if (ThrowOnSend)
                    throw new IOException("Injected packet loss.");
                Packets.Add(packet.ToArray());
            }
        }

        public void AddAttribute(string name, object attribute)
        {
            _attributes.Add(name, attribute);
        }

        public object GetAttribute(string name)
        {
            return _attributes.GetValueOrDefault(name);
        }

        public void ClearAttribute(string name)
        {
            _attributes.Remove(name);
        }

        public void Close()
        {
            Closed = true;
        }
    }
}
