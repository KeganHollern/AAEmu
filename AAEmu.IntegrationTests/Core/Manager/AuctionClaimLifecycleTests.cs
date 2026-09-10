using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.StaticValues;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class AuctionSettlementPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ImmediateClaims_ReloadCompleteSettlementAndProgress(bool sellerFirst, bool mergeStack)
    {
        using var graph = new AuctionGraph();
        ConfigureClaims(graph);
        var item = graph.AddItem();
        Item stack = null;
        if (mergeStack)
            stack = AddBuyerStack(graph, item);
        graph.Post(item);
        var lotId = graph.Auction.AuctionLots.Keys.Single();
        graph.Bid(graph.Buyer, lotId, 2000);
        var buy = graph.OwnMails().Single(mail => mail.MailType == MailType.AucBidWin);
        var sale = graph.OwnMails().Single(mail => mail.MailType == MailType.AucOffSuccess);

        // Neither claim waits for the periodic save. The real auction path commits
        // the buyer debit, lot removal, and both mails before the first request.
        if (sellerFirst)
            Assert.True(graph.Seller.Mails.GetAttached(sale.Id, true, false, true));
        Assert.True(graph.Buyer.Mails.GetAttached(buy.Id, false, true, true));
        if (!sellerFirst)
            Assert.True(graph.Seller.Mails.GetAttached(sale.Id, true, false, true));

        using var restored = new AuctionGraph(graph.Id);
        ConfigureClaims(restored, reload: true);
        Assert.DoesNotContain(lotId, restored.Auction.AuctionLots.Keys);
        Assert.Equal(8000, restored.Buyer.Money);
        Assert.Equal(11780, restored.Seller.Money);
        Assert.Equal(9, restored.Seller.LaborPower);
        Assert.Equal(1, restored.Seller.ConsumedLaborPower);
        Assert.Equal(1u, restored.Seller.Achievements.GetAmount(901));
        Assert.Equal(1u, restored.Buyer.Achievements.GetAmount(900));
        Assert.All(restored.OwnMails(), mail => Assert.Equal(0, mail.Header.Attachments));
        var delivered = restored.Buyer.Inventory.Bag.Items.Single();
        Assert.Equal(mergeStack ? stack.Id : item.Id, delivered.Id);
        Assert.Equal(mergeStack ? 13 : 3, delivered.Count);
        if (mergeStack)
            Assert.Null(restored.Items.GetItemByItemId(item.Id));
        Assert.True(restored.Buyer.Mails.GetAttached(buy.Id, false, true, true));
        Assert.True(restored.Seller.Mails.GetAttached(sale.Id, true, false, true));
        Assert.Equal(11780, restored.Seller.Money);
        Assert.Equal(1u, restored.Buyer.Achievements.GetAmount(900));
        Assert.Equal(1u, restored.Seller.Achievements.GetAmount(901));
    }

    [Fact]
    public void FailedSourceCheckpoint_LeavesClaimPendingUntilRetry()
    {
        using var graph = new AuctionGraph();
        ConfigureClaims(graph);
        var item = graph.AddItem();
        graph.Post(item);
        graph.Bid(graph.Buyer, graph.Auction.AuctionLots.Keys.Single(), 2000);
        var buy = graph.OwnMails().Single(mail => mail.MailType == MailType.AucBidWin);
        var trigger = $"claim_source_failure_{graph.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON characters FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected checkpoint failure'");
        try
        {
            Assert.False(graph.Buyer.Mails.GetAttached(buy.Id, false, true, true));
            Assert.Single(buy.Body.Attachments);
            Assert.Empty(graph.Buyer.Inventory.Bag.Items);
            Assert.Equal(0u, graph.Buyer.Achievements.GetAmount(900));
            Assert.Null(new MySqlAuctionMailClaimStore().FindReceipt(buy.Id, graph.Buyer.Id));
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }
        Assert.True(graph.Buyer.Mails.GetAttached(buy.Id, false, true, true));
        using var restored = new AuctionGraph(graph.Id);
        ConfigureClaims(restored, reload: true);
        Assert.Equal(8000, restored.Buyer.Money);
        Assert.Equal(1u, restored.Buyer.Achievements.GetAmount(900));
        Assert.Single(restored.Buyer.Inventory.Bag.Items);
    }

    [Theory]
    [InlineData("acquire")]
    [InlineData("consume")]
    [InlineData("move")]
    public async Task PausedClaim_ExcludesRealInventoryChangesThroughCommitAndApply(string operation)
    {
        using var graph = new AuctionGraph();
        using var planned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var competitorStarted = new ManualResetEventSlim();
        var store = new PausedClaimStore(planned, release);
        ConfigureClaims(graph, store: store);
        var item = graph.AddItem();
        var stack = AddBuyerStack(graph, item);
        graph.Post(item);
        graph.Bid(graph.Buyer, graph.Auction.AuctionLots.Keys.Single(), 2000);
        var buy = graph.OwnMails().Single(mail => mail.MailType == MailType.AucBidWin);
        var claim = Task.Run(() => graph.Buyer.Mails.GetAttached(buy.Id, false, true, true));
        Task<bool> competing = null;
        try
        {
            Assert.True(planned.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            competing = Task.Run(() =>
            {
                competitorStarted.Set();
                return operation switch
                {
                    "acquire" => graph.Buyer.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Invalid, 100, 2, 6),
                    "consume" => graph.Buyer.Inventory.Bag.TryConsumeItems(ItemTaskType.Invalid, new Dictionary<uint, int> { [100] = 2 }),
                    "move" => graph.Buyer.Inventory.GetContainer(SlotType.Bank).AddOrMoveExistingItem(ItemTaskType.Invalid, stack),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
            });
            Assert.True(competitorStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(competing.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        Assert.True(await claim.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(await competing!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(graph.Save.TryCommitEconomy([graph.Buyer]));
        using var restored = new AuctionGraph(graph.Id);
        ConfigureClaims(restored, reload: true);
        var delivered = restored.Items.GetItemByItemId(stack.Id);
        Assert.Equal(operation == "acquire" ? 15 : operation == "consume" ? 11 : 13, delivered.Count);
        Assert.Equal(operation == "move" ? SlotType.Bank : SlotType.Inventory, delivered.SlotType);
        Assert.Equal(1u, restored.Buyer.Achievements.GetAmount(900));
        Assert.Empty(restored.OwnMails().Single(mail => mail.Id == buy.Id).Body.Attachments);
        Assert.Equal(8000, restored.Buyer.Money);
    }

    private static Item AddBuyerStack(AuctionGraph graph, Item source)
    {
        var item = source.CopyForSplit(graph.Id + 5, 10);
        item.OwnerId = graph.Buyer.Id;
        item.Slot = 0;
        item._holdingContainer = graph.Buyer.Inventory.Bag;
        graph.Items.AddItem(item);
        graph.Buyer.Inventory.Bag.Items.Add(item);
        graph.Buyer.Inventory.Bag.UpdateFreeSlotCount();
        return item;
    }

    private static void ConfigureClaims(AuctionGraph graph, bool reload = false, IAuctionMailClaimStore store = null)
    {
        var data = new AchievementGameData();
        SetPrivate(data, "_charRecords", new Dictionary<uint, CharRecords>
        {
            [900] = new() { Id = 900, KindId = CharRecordKind.AuctionBuy },
            [901] = new() { Id = 901, KindId = CharRecordKind.AuctionSold }
        });
        SetPrivate(data, "_achievements", new Dictionary<uint, Achievements>
        {
            [900] = new() { Id = 900, CompleteNum = 100, IsActive = true },
            [901] = new() { Id = 901, CompleteNum = 100, IsActive = true }
        });
        SetPrivate(data, "_achievementObjectives", new Dictionary<uint, List<AchievementObjectives>>
        {
            [900] = [new() { Id = 900, AchievementId = 900, RecordId = 900 }],
            [901] = [new() { Id = 901, AchievementId = 901, RecordId = 901 }]
        });
        data.PostLoad();
        var manager = new AuctionMailClaimManager(store ?? new MySqlAuctionMailClaimStore(),
            accountSyncRoot: _ => SaveManager.PersistenceSyncRoot,
            salePlanFactory: TestSalePlan,
            forgetCommittedItem: graph.Items.ForgetCommittedItem,
            retainCommittedItemId: _ => { },
            stopForConsistencyFailure: (message, exception) => throw new InvalidOperationException(message, exception),
            persistSourceState: character => graph.Save.TryCommitEconomy([character]));
        foreach (var character in new[] { graph.Buyer, graph.Seller, graph.Other })
        {
            character.InitializeLaborCache(10, DateTime.UtcNow);
            character.Actability = new CharacterActability(character);
            character.Actability.Actabilities[(uint)ActabilityType.Commerce] = new(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce });
            character.Abilities = new CharacterAbilities(character);
            var achievements = new CharacterAchievements(character, data, TimeProvider.System, null,
                unitRequirementsData: new UnitRequirementsGameData());
            typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(character, achievements);
            character.Mails = new CharacterMails(character, manager);
            if (!reload)
                Execute($"INSERT INTO accounts (account_id,labor) VALUES ({character.AccountId},10)");
            else
            {
                using var connection = MySQL.CreateConnection();
                character.Achievements.Load(connection);
                character.Achievements.ReconcileAuthoritativeState();
                character.Abilities.Load(connection);
                character.ConsumedLaborPower = (int)Read("characters", "consumed_lp", character.Id);
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT labor FROM accounts WHERE account_id={character.AccountId}";
                character.InitializeLaborCache(Convert.ToInt16(command.ExecuteScalar()), DateTime.UtcNow);
            }
        }
    }

    private static AuctionSaleClaimPlan TestSalePlan(Character character, BaseMail mail, DateTime now)
    {
        var commerce = character.Actability.Actabilities[(uint)ActabilityType.Commerce];
        return new AuctionSaleClaimPlan(character, mail,
            new(mail.Id, AuctionMailClaimType.SaleMoney, character.Id, null, null, null, null, mail.Body.CopperCoins),
            MailStatus.Read, now, 0, [], now, character.Money, character.Money + mail.Body.CopperCoins,
            character.LaborPower, (short)(character.LaborPower - 1), character.Experience, character.Experience, 0,
            character.Level, character.Level, character.ConsumedLaborPower + 1, (uint)ActabilityType.Commerce,
            commerce.Point, commerce.Point + 1, 1, commerce.Step,
            character.Abilities.Abilities.ToDictionary(pair => pair.Key, pair => pair.Value.Exp),
            character.Abilities.Abilities.ToDictionary(pair => pair.Key, _ => character.Level));
    }

    private static void SetPrivate(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private sealed class PausedClaimStore(ManualResetEventSlim planned, ManualResetEventSlim release) : IAuctionMailClaimStore
    {
        private readonly MySqlAuctionMailClaimStore _inner = new();
        public AuctionMailClaimReceipt FindReceipt(long mailId, uint receiverId) => _inner.FindReceipt(mailId, receiverId);
        public AuctionMailClaimPersistenceResult Persist(AuctionMailClaimPlan plan, CharacterAchievements achievements)
        {
            planned.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The claim test did not release the prepared plan.");
            return _inner.Persist(plan, achievements);
        }
    }
}
