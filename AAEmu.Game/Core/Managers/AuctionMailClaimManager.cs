using System.Diagnostics.CodeAnalysis;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.StaticValues;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

internal enum AuctionMailClaimResult
{
    NotHandled,
    Success,
    Failure
}

internal enum AuctionMailClaimType : byte
{
    BuyItem = 1,
    SaleMoney = 2
}

internal interface IAuctionMailClaimManager
{
    AuctionMailClaimResult TryClaim(
        Character character,
        CharacterMails mails,
        long mailId,
        bool takeMoney,
        bool takeItems,
        bool takeAllSelected,
        ulong specifiedItemId);
}

internal interface IAuctionMailClaimStore
{
    AuctionMailClaimReceipt FindReceipt(long mailId, uint receiverId);
    AuctionMailClaimPersistenceResult Persist(
        AuctionMailClaimPlan plan,
        CharacterAchievements achievements);
}

internal enum AuctionMailClaimPersistenceResult
{
    Created,
    Replay
}

internal sealed record AuctionMailClaimReceipt(
    long MailId,
    AuctionMailClaimType ClaimType,
    uint ReceiverId,
    ulong? ItemId,
    int? ItemCount,
    SlotType? ItemSlotType,
    byte? ItemSlot,
    long? MoneyAmount);

internal abstract record AuctionMailClaimPlan(
    Character Character,
    BaseMail Mail,
    AuctionMailClaimReceipt Receipt,
    MailStatus FinalMailStatus,
    DateTime FinalOpenDate,
    byte FinalAttachmentCount,
    IReadOnlyList<Item> RemainingAttachments,
    DateTime UpdatedAt)
{
    public bool MarkedRead { get; } = Mail.Header.Status == MailStatus.Unread;
}

internal sealed record AuctionBuyClaimPlan(
    Character Character,
    BaseMail Mail,
    AuctionMailClaimReceipt Receipt,
    MailStatus FinalMailStatus,
    DateTime FinalOpenDate,
    byte FinalAttachmentCount,
    IReadOnlyList<Item> RemainingAttachments,
    DateTime UpdatedAt,
    Item SourceItem,
    Item DestinationStack,
    int DestinationSlot,
    int DestinationCountBefore,
    int DestinationCountAfter,
    ItemFlag ItemFlagsBefore,
    ItemFlag FinalItemFlags)
    : AuctionMailClaimPlan(
        Character,
        Mail,
        Receipt,
        FinalMailStatus,
        FinalOpenDate,
        FinalAttachmentCount,
        RemainingAttachments,
        UpdatedAt);

internal sealed record AuctionSaleClaimPlan(
    Character Character,
    BaseMail Mail,
    AuctionMailClaimReceipt Receipt,
    MailStatus FinalMailStatus,
    DateTime FinalOpenDate,
    byte FinalAttachmentCount,
    IReadOnlyList<Item> RemainingAttachments,
    DateTime UpdatedAt,
    long MoneyBefore,
    long MoneyAfter,
    short LaborBefore,
    short LaborAfter,
    int ExperienceBefore,
    int ExperienceAfter,
    int ExperienceChange,
    byte LevelBefore,
    byte LevelAfter,
    int ConsumedLaborAfter,
    uint ActabilityId,
    int ActabilityPointBefore,
    int ActabilityPointAfter,
    int ActabilityPointChange,
    byte ActabilityStep,
    IReadOnlyDictionary<AbilityType, int> AbilityExperienceAfter,
    IReadOnlyDictionary<AbilityType, byte> AbilityLevelsAfter)
    : AuctionMailClaimPlan(
        Character,
        Mail,
        Receipt,
        FinalMailStatus,
        FinalOpenDate,
        FinalAttachmentCount,
        RemainingAttachments,
        UpdatedAt);

internal sealed class AuctionMailClaimManager : IAuctionMailClaimManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IAuctionMailClaimStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Func<uint, object> _accountSyncRoot;
    private readonly Func<Character, BaseMail, DateTime, AuctionSaleClaimPlan> _salePlanFactory;
    private readonly Action<ulong> _forgetCommittedItem;
    private readonly Action<ulong> _retainCommittedItemId;
    private readonly Action<string, Exception> _stopForConsistencyFailure;

    public static AuctionMailClaimManager Instance { get; } = new(
        new MySqlAuctionMailClaimStore());

    internal AuctionMailClaimManager(
        IAuctionMailClaimStore store,
        TimeProvider timeProvider = null,
        Func<uint, object> accountSyncRoot = null,
        Func<Character, BaseMail, DateTime, AuctionSaleClaimPlan> salePlanFactory = null,
        Action<ulong> forgetCommittedItem = null,
        Action<ulong> retainCommittedItemId = null,
        Action<string, Exception> stopForConsistencyFailure = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _accountSyncRoot = accountSyncRoot ??
            (accountId => AccountManager.Instance.GetAccountSyncRoot(accountId));
        _salePlanFactory = salePlanFactory ?? CreateSalePlan;
        _forgetCommittedItem = forgetCommittedItem ??
            (itemId => ItemManager.Instance.ForgetCommittedItem(itemId));
        _retainCommittedItemId = retainCommittedItemId ??
            (itemId => ItemIdManager.Instance.RetainId(checked((uint)itemId)));
        _stopForConsistencyFailure = stopForConsistencyFailure ?? StopForConsistencyFailure;
    }

    public AuctionMailClaimResult TryClaim(
        Character character,
        CharacterMails mails,
        long mailId,
        bool takeMoney,
        bool takeItems,
        bool takeAllSelected,
        ulong specifiedItemId)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(mails);

        MailManager.Instance._allPlayerMails.TryGetValue(mailId, out var mail);
        if (mail != null && mail.MailType is not (MailType.AucBidWin or MailType.AucOffSuccess))
            return AuctionMailClaimResult.NotHandled;

        if (mail == null)
        {
            AuctionMailClaimReceipt receipt;
            try
            {
                receipt = _store.FindReceipt(mailId, character.Id);
            }
            catch (Exception ex)
            {
                LogReceiptLookupFailure(ex, mailId, character.Id);
                return AuctionMailClaimResult.Failure;
            }

            if (receipt == null || !IsRequested(receipt.ClaimType, takeMoney, takeItems))
                return AuctionMailClaimResult.NotHandled;

            PublishReceipt(character, mails, receipt, takeAllSelected);
            return AuctionMailClaimResult.Success;
        }

        if (mail.Header.ReceiverId != character.Id)
            return AuctionMailClaimResult.NotHandled;

        return mail.MailType switch
        {
            MailType.AucOffSuccess when takeMoney =>
                ClaimSale(character, mails, mail, takeAllSelected),
            MailType.AucBidWin when takeItems =>
                ClaimBuy(character, mails, mail, takeAllSelected, specifiedItemId),
            _ => AuctionMailClaimResult.NotHandled
        };
    }

    private AuctionMailClaimResult ClaimSale(
        Character character,
        CharacterMails mails,
        BaseMail mail,
        bool takeAllSelected)
    {
        AuctionMailClaimReceipt receipt = null;
        AuctionSaleClaimPlan committedPlan = null;
        var accountSyncRoot = _accountSyncRoot(character.AccountId);

        lock (character.StorePurchaseSyncRoot)
        lock (SaveManager.PersistenceSyncRoot)
        lock (accountSyncRoot)
        {
            try
            {
                receipt = _store.FindReceipt(mail.Id, character.Id);
            }
            catch (Exception ex)
            {
                LogReceiptLookupFailure(ex, mail.Id, character.Id);
                return AuctionMailClaimResult.Failure;
            }

            if (receipt != null)
            {
                if (receipt.ClaimType != AuctionMailClaimType.SaleMoney)
                    return AuctionMailClaimResult.Failure;
                if (!IsReceiptAppliedToLocalMail(mail, receipt))
                {
                    _stopForConsistencyFailure(
                        $"Auction sale claim {mail.Id} for character {character.Id} was committed by another Game instance while this instance still held unclaimed mail state. This instance must restart before another save.",
                        new InvalidOperationException("A durable auction sale receipt conflicts with the local mail payload."));
                    return AuctionMailClaimResult.Failure;
                }
            }
            else
            {
                if (!ValidateSaleMail(character, mail))
                    return AuctionMailClaimResult.Failure;

                using var achievementScope = character.Achievements.BeginDeferredPersistence();
                try
                {
                    var plan = _salePlanFactory(character, mail, _timeProvider.GetUtcNow().UtcDateTime);
                    StageSaleAchievements(character, plan);

                    var persistenceResult = _store.Persist(plan, character.Achievements);
                    if (persistenceResult == AuctionMailClaimPersistenceResult.Replay)
                    {
                        _stopForConsistencyFailure(
                            $"Auction sale claim {mail.Id} for character {character.Id} was committed by another Game instance. This instance must restart before another save.",
                            new InvalidOperationException("A concurrent auction sale claim won after the local receipt lookup."));
                        return AuctionMailClaimResult.Failure;
                    }

                    receipt = plan.Receipt;
                    try
                    {
                        ApplyCommittedSaleState(plan, mails);
                        achievementScope.Commit();
                        committedPlan = plan;
                    }
                    catch (Exception ex)
                    {
                        _stopForConsistencyFailure(
                            $"Committed auction sale claim {mail.Id} for character {character.Id} could not enter live state. The Game service must restart before another save.",
                            ex);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex,
                        "Failed to commit auction sale claim {MailId} for character {CharacterId}",
                        mail.Id,
                        character.Id);
                    return AuctionMailClaimResult.Failure;
                }
            }
        }

        if (receipt == null)
            return AuctionMailClaimResult.Failure;

        PublishReceipt(character, mails, receipt, takeAllSelected, committedPlan);
        return AuctionMailClaimResult.Success;
    }

    private AuctionMailClaimResult ClaimBuy(
        Character character,
        CharacterMails mails,
        BaseMail mail,
        bool takeAllSelected,
        ulong specifiedItemId)
    {
        AuctionMailClaimReceipt receipt = null;
        AuctionBuyClaimPlan committedPlan = null;

        lock (character.StorePurchaseSyncRoot)
        lock (SaveManager.PersistenceSyncRoot)
        {
            try
            {
                receipt = _store.FindReceipt(mail.Id, character.Id);
            }
            catch (Exception ex)
            {
                LogReceiptLookupFailure(ex, mail.Id, character.Id);
                return AuctionMailClaimResult.Failure;
            }

            if (receipt != null)
            {
                if (receipt.ClaimType != AuctionMailClaimType.BuyItem)
                    return AuctionMailClaimResult.Failure;
                if (!IsReceiptAppliedToLocalMail(mail, receipt))
                {
                    _stopForConsistencyFailure(
                        $"Auction buy claim {mail.Id} for character {character.Id} was committed by another Game instance while this instance still held unclaimed mail state. This instance must restart before another save.",
                        new InvalidOperationException("A durable auction buy receipt conflicts with the local mail payload."));
                    return AuctionMailClaimResult.Failure;
                }
            }
            else
            {
                if (!TryCreateBuyPlan(
                        character,
                        mail,
                        specifiedItemId,
                        _timeProvider.GetUtcNow().UtcDateTime,
                        out var plan))
                {
                    return AuctionMailClaimResult.Failure;
                }

                using var achievementScope = character.Achievements.BeginDeferredPersistence();
                try
                {
                    StageBuyAchievements(character, plan);
                    var persistenceResult = _store.Persist(plan, character.Achievements);
                    if (persistenceResult == AuctionMailClaimPersistenceResult.Replay)
                    {
                        _stopForConsistencyFailure(
                            $"Auction buy claim {mail.Id} for character {character.Id} was committed by another Game instance. This instance must restart before another save.",
                            new InvalidOperationException("A concurrent auction buy claim won after the local receipt lookup."));
                        return AuctionMailClaimResult.Failure;
                    }

                    receipt = plan.Receipt;
                    try
                    {
                        _retainCommittedItemId(plan.SourceItem.Id);
                        ApplyCommittedBuyState(plan, mails);
                        achievementScope.Commit();
                        committedPlan = plan;
                    }
                    catch (Exception ex)
                    {
                        _stopForConsistencyFailure(
                            $"Committed auction buy claim {mail.Id} for character {character.Id} could not enter live state. The Game service must restart before another save.",
                            ex);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex,
                        "Failed to commit auction buy claim {MailId} for character {CharacterId}",
                        mail.Id,
                        character.Id);
                    return AuctionMailClaimResult.Failure;
                }
            }
        }

        if (receipt == null)
            return AuctionMailClaimResult.Failure;

        PublishReceipt(character, mails, receipt, takeAllSelected, committedPlan);
        return AuctionMailClaimResult.Success;
    }

    private static bool ValidateSaleMail(Character character, BaseMail mail)
    {
        if (mail.Header.ReceiverId != character.Id ||
            mail.MailType != MailType.AucOffSuccess ||
            mail.Body.CopperCoins <= 0)
        {
            return false;
        }

        if (character.LaborPower >= 1)
            return true;

        character.SendErrorMessage(ErrorMessageType.NotEnoughLaborPower);
        return false;
    }

    internal static bool TryCreateBuyPlan(
        Character character,
        BaseMail mail,
        ulong specifiedItemId,
        DateTime now,
        [NotNullWhen(true)] out AuctionBuyClaimPlan plan)
    {
        plan = null;
        if (mail.Header.ReceiverId != character.Id || mail.MailType != MailType.AucBidWin)
            return false;

        var sourceItem = specifiedItemId == 0
            ? mail.Body.Attachments.FirstOrDefault()
            : mail.Body.Attachments.FirstOrDefault(item => item.Id == specifiedItemId);
        if (sourceItem == null || sourceItem.Id == 0)
            return false;

        var bag = character.Inventory?.Bag;
        if (bag == null || bag.SpaceLeftForItem(sourceItem, out var foundItems) < sourceItem.Count)
        {
            character.SendErrorMessage(ErrorMessageType.BagFull);
            return false;
        }

        var destinationStack = sourceItem.Template.MaxCount > 1
            ? foundItems.FirstOrDefault(item =>
                !ReferenceEquals(item, sourceItem) &&
                ReferenceEquals(item._holdingContainer, bag) &&
                item.Count + sourceItem.Count <= item.Template.MaxCount)
            : null;
        var destinationSlot = destinationStack?.Slot ?? bag.GetUnusedSlot(-1);
        if (destinationSlot < 0)
        {
            character.SendErrorMessage(ErrorMessageType.BagFull);
            return false;
        }

        var sourceSlot = checked((byte)sourceItem.Slot);
        var itemFlags = destinationStack?.ItemFlags ?? sourceItem.ItemFlags;
        if (sourceItem.Template.BindType == ItemBindType.BindOnPickup)
            itemFlags |= ItemFlag.SoulBound;

        var remainingAttachments = mail.Body.Attachments
            .Where(item => !ReferenceEquals(item, sourceItem))
            .ToArray();
        var finalAttachmentCount = checked((byte)(mail.Header.Attachments - 1));
        var finalStatus = mail.Header.Status == MailStatus.Unread
            ? MailStatus.Read
            : mail.Header.Status;
        var finalOpenDate = mail.Header.Status == MailStatus.Unread ? now : mail.OpenDate;
        var receipt = new AuctionMailClaimReceipt(
            mail.Id,
            AuctionMailClaimType.BuyItem,
            character.Id,
            sourceItem.Id,
            sourceItem.Count,
            sourceItem.SlotType,
            sourceSlot,
            null);

        plan = new AuctionBuyClaimPlan(
            character,
            mail,
            receipt,
            finalStatus,
            finalOpenDate,
            finalAttachmentCount,
            remainingAttachments,
            now,
            sourceItem,
            destinationStack,
            destinationSlot,
            destinationStack?.Count ?? 0,
            (destinationStack?.Count ?? 0) + sourceItem.Count,
            destinationStack?.ItemFlags ?? sourceItem.ItemFlags,
            itemFlags);
        return true;
    }

    internal static AuctionSaleClaimPlan CreateSalePlan(
        Character character,
        BaseMail mail,
        DateTime now)
    {
        var actabilityId = (uint)ActabilityType.Commerce;
        var commerce = character.Actability.Actabilities[actabilityId];
        var actabilityPointChange = (int)AppConfiguration.Instance.World.ActabilityRate;
        var expertLimit = UnitManagers.CharacterManager.Instance.GetExpertLimit(commerce.Step);
        var actabilityPointAfter = Math.Min(
            checked(commerce.Point + actabilityPointChange),
            expertLimit.UpLimit);
        actabilityPointChange = actabilityPointAfter - commerce.Point;

        var parameters = new Dictionary<string, double>
        {
            ["labor_power"] = 1,
            ["pc_level"] = character.Level
        };
        var formula = FormulaManager.Instance.GetFormula((uint)FormulaKind.ExpByLaborPower);
        var experienceChange = (int)(formula.Evaluate(parameters) * commerce.GetExpMultiplier());
        if (experienceChange > 0)
            experienceChange = (int)(experienceChange * AppConfiguration.Instance.World.ExpRate);

        var experienceAfter = checked(character.Experience + experienceChange);
        var levelAfter = ExperienceManager.Instance.GetLevelFromExp(
            experienceAfter,
            character.Level,
            out var overflow);
        if (levelAfter >= ExperienceManager.Instance.MaxPlayerLevel)
            experienceAfter -= overflow;

        var abilityExperienceAfter = character.Abilities.Abilities
            .ToDictionary(pair => pair.Key, pair => pair.Value.Exp);
        var maximumAbilityExperience = ExperienceManager.Instance.GetExpForLevel(
            ExperienceManager.Instance.MaxPlayerLevel);
        foreach (var abilityId in new[] { character.Ability1, character.Ability2, character.Ability3 })
        {
            if (abilityId == AbilityType.None)
                continue;
            abilityExperienceAfter[abilityId] = Math.Min(
                checked(abilityExperienceAfter[abilityId] + experienceChange),
                maximumAbilityExperience);
        }

        var abilityLevelsAfter = abilityExperienceAfter.ToDictionary(
            pair => pair.Key,
            pair => ExperienceManager.Instance.GetLevelFromExp(pair.Value, out _));
        var finalStatus = mail.Header.Status == MailStatus.Unread
            ? MailStatus.Read
            : mail.Header.Status;
        var finalOpenDate = mail.Header.Status == MailStatus.Unread ? now : mail.OpenDate;
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

        return new AuctionSaleClaimPlan(
            character,
            mail,
            receipt,
            finalStatus,
            finalOpenDate,
            checked((byte)(mail.Header.Attachments - 1)),
            mail.Body.Attachments.ToArray(),
            now,
            character.Money,
            moneyAfter,
            character.LaborPower,
            checked((short)(character.LaborPower - 1)),
            character.Experience,
            experienceAfter,
            experienceChange,
            character.Level,
            levelAfter,
            (int)Math.Min((long)Math.Max(character.ConsumedLaborPower, 0) + 1, int.MaxValue),
            actabilityId,
            commerce.Point,
            actabilityPointAfter,
            actabilityPointChange,
            commerce.Step,
            abilityExperienceAfter,
            abilityLevelsAfter);
    }

    private static void StageBuyAchievements(Character character, AuctionBuyClaimPlan plan)
    {
        character.Achievements.Increment(
            CharRecordKind.GetItemType,
            plan.SourceItem.TemplateId,
            plan.SourceItem.Grade,
            (uint)plan.SourceItem.Count,
            matchValue2Wildcard: true);
        character.Achievements.Increment(CharRecordKind.AuctionBuy, 0, 0);
    }

    private static void StageSaleAchievements(Character character, AuctionSaleClaimPlan plan)
    {
        character.Achievements.UpdateMaximum(
            CharRecordKind.GetActability,
            plan.ActabilityId,
            0,
            (uint)Math.Max(plan.ActabilityPointAfter, 0));
        if (plan.ExperienceChange != 0)
        {
            character.Achievements.UpdateLevel(plan.LevelAfter);
            foreach (var abilityId in new[] { character.Ability1, character.Ability2, character.Ability3 })
            {
                if (abilityId != AbilityType.None)
                    character.Achievements.UpdateAbilityLevel(
                        abilityId,
                        plan.AbilityLevelsAfter[abilityId]);
            }
        }
        character.Achievements.UpdateMaximum(
            CharRecordKind.SpendLabor,
            0,
            0,
            (uint)Math.Max(plan.ConsumedLaborAfter, 0));
        var gold = plan.MoneyAfter <= 0
            ? 0u
            : (uint)Math.Min(plan.MoneyAfter / 10000L, uint.MaxValue);
        character.Achievements.UpdateMaximum(CharRecordKind.MyGold, 0, 0, gold);
        character.Achievements.Increment(CharRecordKind.AuctionSold, 0, 0);
    }

    private void ApplyCommittedBuyState(AuctionBuyClaimPlan plan, CharacterMails mails)
    {
        var bag = plan.Character.Inventory.Bag;
        if (!plan.Mail.Body.Attachments.Contains(plan.SourceItem))
            throw new InvalidOperationException("The committed auction attachment left its mail before live apply.");
        if (plan.DestinationStack != null &&
            (plan.DestinationStack.Count != plan.DestinationCountBefore ||
             plan.DestinationStack.Slot != plan.DestinationSlot ||
             !ReferenceEquals(plan.DestinationStack._holdingContainer, bag)))
        {
            throw new InvalidOperationException("The committed auction destination stack changed before live apply.");
        }

        if (!bag.AddOrMoveExistingItem(
                ItemTaskType.Invalid,
                plan.SourceItem,
                plan.DestinationSlot,
                notifyInventory: false,
                applyBindRules: false))
        {
            throw new InvalidOperationException("The committed auction item could not enter its inventory slot.");
        }

        var deliveredItem = plan.DestinationStack ?? plan.SourceItem;
        if (deliveredItem.Count != plan.DestinationCountAfter ||
            deliveredItem.Slot != plan.DestinationSlot ||
            !ReferenceEquals(deliveredItem._holdingContainer, bag))
        {
            throw new InvalidOperationException("The committed auction item entered a different live state.");
        }

        deliveredItem.ItemFlags = plan.FinalItemFlags;
        deliveredItem.IsDirty = false;
        if (plan.DestinationStack != null)
            _forgetCommittedItem(plan.SourceItem.Id);

        ApplyCommittedMailState(plan, mails);
    }

    private static void ApplyCommittedSaleState(AuctionSaleClaimPlan plan, CharacterMails mails)
    {
        var character = plan.Character;
        character.ApplyCommittedAuctionSaleState(
            plan.MoneyAfter,
            plan.LaborAfter,
            plan.ExperienceAfter,
            plan.LevelAfter,
            plan.ConsumedLaborAfter,
            plan.UpdatedAt);
        character.Actability.Actabilities[plan.ActabilityId].Point = plan.ActabilityPointAfter;
        foreach (var (abilityId, experience) in plan.AbilityExperienceAfter)
            character.Abilities.Abilities[abilityId].Exp = experience;

        ApplyCommittedMailState(plan, mails);
    }

    private static void ApplyCommittedMailState(
        AuctionMailClaimPlan plan,
        CharacterMails mails)
    {
        var mail = plan.Mail;
        mail.Body.CopperCoins = plan is AuctionSaleClaimPlan ? 0 : mail.Body.CopperCoins;
        mail.Body.Attachments.Clear();
        mail.Body.Attachments.AddRange(plan.RemainingAttachments);
        mail.Header.Attachments = plan.FinalAttachmentCount;
        mail.Header.Status = plan.FinalMailStatus;
        mail.OpenDate = plan.FinalOpenDate;
        mail.IsDirty = false;

        if (plan.MarkedRead)
            mails.UnreadMailCount.UpdateReceived(mail.MailType, -1);
    }

    private static bool IsRequested(
        AuctionMailClaimType claimType,
        bool takeMoney,
        bool takeItems) => claimType switch
    {
        AuctionMailClaimType.BuyItem => takeItems,
        AuctionMailClaimType.SaleMoney => takeMoney,
        _ => false
    };

    private static bool IsReceiptAppliedToLocalMail(
        BaseMail mail,
        AuctionMailClaimReceipt receipt) => receipt.ClaimType switch
    {
        AuctionMailClaimType.SaleMoney => mail.Body.CopperCoins == 0,
        AuctionMailClaimType.BuyItem => receipt.ItemId.HasValue &&
                                        mail.Body.Attachments.All(item => item.Id != receipt.ItemId.Value),
        _ => false
    };

    private static void LogReceiptLookupFailure(Exception exception, long mailId, uint characterId)
    {
        Logger.Error(exception,
            "Failed to look up auction mail claim {MailId} for character {CharacterId}",
            mailId,
            characterId);
    }

    private static void PublishReceipt(
        Character character,
        CharacterMails mails,
        AuctionMailClaimReceipt receipt,
        bool takeAllSelected,
        AuctionMailClaimPlan committedPlan = null)
    {
        RunCommittedSideEffects(character, receipt, committedPlan);

        try
        {
            if (committedPlan is AuctionSaleClaimPlan salePlan)
            {
                if (salePlan.ExperienceChange != 0)
                {
                    character.SendPacket(new SCExpChangedPacket(
                        character.ObjId,
                        salePlan.ExperienceChange,
                        true));
                    if (salePlan.LevelAfter > salePlan.LevelBefore)
                    {
                        character.BroadcastPacket(
                            new SCLevelChangedPacket(character.ObjId, salePlan.LevelAfter),
                            true);
                    }
                }

                character.SendPacket(new SCCharacterLaborPowerChangedPacket(
                    salePlan.LaborAfter - salePlan.LaborBefore,
                    (int)ActabilityType.Commerce,
                    salePlan.ActabilityPointChange,
                    salePlan.ActabilityStep));
                character.SendPacket(new SCItemTaskSuccessPacket(
                    ItemTaskType.DepositMoney,
                    new MoneyChange(checked((int)receipt.MoneyAmount!.Value)),
                    []));
            }
            else if (committedPlan is AuctionBuyClaimPlan buyPlan)
            {
                var deliveredItem = buyPlan.DestinationStack ?? buyPlan.SourceItem;
                ItemTask itemTask = buyPlan.DestinationStack == null
                    ? new ItemAdd(deliveredItem)
                    : new ItemCountUpdate(deliveredItem, buyPlan.SourceItem.Count);
                character.SendPacket(new SCItemTaskSuccessPacket(
                    ItemTaskType.Mail,
                    itemTask,
                    []));
                if (buyPlan.ItemFlagsBefore != buyPlan.FinalItemFlags)
                {
                    character.SendPacket(new SCItemTaskSuccessPacket(
                        ItemTaskType.Mail,
                        new ItemUpdateBits(deliveredItem),
                        []));
                }

            }

            if (receipt.ClaimType == AuctionMailClaimType.BuyItem)
            {
                var item = new ItemIdAndLocation
                {
                    Id = receipt.ItemId!.Value,
                    SlotType = receipt.ItemSlotType!.Value,
                    Slot = receipt.ItemSlot!.Value
                };
                character.SendPacket(new SCAttachmentTakenPacket(
                    receipt.MailId,
                    false,
                    false,
                    takeAllSelected,
                    [item]));
            }
            else
            {
                character.SendPacket(new SCAttachmentTakenPacket(
                    receipt.MailId,
                    true,
                    false,
                    takeAllSelected,
                    []));
            }

            character.SendPacket(new SCMailStatusUpdatedPacket(
                false,
                receipt.MailId,
                MailStatus.Read));
            mails.SendUnreadMailCount();
            character.Achievements.SendCommittedState();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "Auction mail claim {MailId} committed for character {CharacterId}, but a post-commit packet failed; closing the session for an authoritative reload",
                receipt.MailId,
                character.Id);
            try
            {
                character.Connection?.Shutdown();
            }
            catch (Exception shutdownException)
            {
                Logger.Error(shutdownException,
                    "Failed to close the session after auction claim packet failure for character {CharacterId}",
                    character.Id);
            }
        }
    }

    private static void RunCommittedSideEffects(
        Character character,
        AuctionMailClaimReceipt receipt,
        AuctionMailClaimPlan committedPlan)
    {
        try
        {
            switch (committedPlan)
            {
                case AuctionSaleClaimPlan salePlan when salePlan.ExperienceChange != 0:
                    if (salePlan.LevelAfter > salePlan.LevelBefore)
                        character.Expedition?.OnCharacterRefresh(character);
                    if (character.Connection != null)
                        QuestManager.Instance.DoOnLevelUpEvents(character.Connection.ActiveChar);
                    break;
                case AuctionBuyClaimPlan buyPlan:
                    QuestManager.Instance.DoItemsAcquiredEvents(
                        character,
                        buyPlan.SourceItem.TemplateId,
                        buyPlan.SourceItem.Count);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "Auction mail claim {MailId} committed for character {CharacterId}, but a post-commit gameplay callback failed",
                receipt.MailId,
                character.Id);
        }

        if (committedPlan?.MarkedRead != true ||
            !MailManager.Instance._allPlayerMails.TryGetValue(receipt.MailId, out var mail))
        {
            return;
        }

        try
        {
            MailManager.Instance.NotifyMailReceiverOpenedIfSenderOnline(mail);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "Auction mail claim {MailId} committed for character {CharacterId}, but its sender-open notification failed",
                receipt.MailId,
                character.Id);
        }
    }

    [DoesNotReturn]
    private static void StopForConsistencyFailure(string message, Exception exception)
    {
        Logger.Fatal(exception, message);
        Environment.FailFast(message, exception);
    }
}

internal sealed class MySqlAuctionMailClaimStore : IAuctionMailClaimStore
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public AuctionMailClaimReceipt FindReceipt(long mailId, uint receiverId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `mail_id`,`claim_type`,`receiver_id`,`item_id`,`item_count`,`item_slot_type`,`item_slot`,`money_amount` " +
            "FROM `auction_mail_claims` WHERE `mail_id` = @mail_id AND `receiver_id` = @receiver_id " +
            "ORDER BY `claim_type` LIMIT 1";
        command.Parameters.AddWithValue("@mail_id", CheckedMailId(mailId));
        command.Parameters.AddWithValue("@receiver_id", receiverId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReceipt(reader) : null;
    }

    public AuctionMailClaimPersistenceResult Persist(
        AuctionMailClaimPlan plan,
        CharacterAchievements achievements)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var commitAttempted = false;
        try
        {
            // Reserve the idempotency key before touching claim state. A concurrent claimant
            // waits on this unique insert instead of sharing a missing-row gap lock and then
            // deadlocking while both transactions try to insert the same receipt.
            InsertReceipt(connection, transaction, plan.Receipt);
            WriteMail(connection, transaction, plan);
            switch (plan)
            {
                case AuctionBuyClaimPlan buyPlan:
                    WriteBuyState(connection, transaction, buyPlan);
                    break;
                case AuctionSaleClaimPlan salePlan:
                    WriteSaleState(connection, transaction, salePlan);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(plan));
            }
            achievements.Save(connection, transaction);
            commitAttempted = true;
            transaction.Commit();
            return AuctionMailClaimPersistenceResult.Created;
        }
        catch (Exception ex)
        {
            if (commitAttempted)
            {
                var message =
                    $"Auction mail claim {plan.Receipt.MailId}/{plan.Receipt.ClaimType} for character {plan.Character.Id} has an unknown commit result. The Game service must restart before another save.";
                Logger.Fatal(ex, message);
                Environment.FailFast(message, ex);
            }

            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackException)
            {
                Logger.Error(rollbackException,
                    "Failed to roll back auction mail claim {MailId}",
                    plan.Receipt.MailId);
            }

            if (ex is MySqlException { Number: 1205 or 1213 })
            {
                var message =
                    $"Auction mail claim {plan.Receipt.MailId}/{plan.Receipt.ClaimType} for character {plan.Character.Id} lost a database lock race. Another Game instance may have committed competing state, so this instance must restart before another save.";
                Logger.Fatal(ex, message);
                Environment.FailFast(message, ex);
            }

            if (ex is MySqlException { Number: 1062 })
            {
                var existing = FindReceipt(plan.Receipt.MailId, plan.Receipt.ReceiverId);
                if (existing != null)
                {
                    EnsureMatchingReceipt(plan.Receipt, existing);
                    return AuctionMailClaimPersistenceResult.Replay;
                }
            }

            throw;
        }
    }

    private static AuctionMailClaimReceipt ReadReceipt(MySqlDataReader reader)
    {
        var itemIdOrdinal = reader.GetOrdinal("item_id");
        var itemCountOrdinal = reader.GetOrdinal("item_count");
        var itemSlotTypeOrdinal = reader.GetOrdinal("item_slot_type");
        var itemSlotOrdinal = reader.GetOrdinal("item_slot");
        var moneyAmountOrdinal = reader.GetOrdinal("money_amount");
        return new AuctionMailClaimReceipt(
            reader.GetUInt32("mail_id"),
            (AuctionMailClaimType)reader.GetByte("claim_type"),
            reader.GetUInt32("receiver_id"),
            reader.IsDBNull(itemIdOrdinal) ? null : reader.GetUInt64(itemIdOrdinal),
            reader.IsDBNull(itemCountOrdinal) ? null : reader.GetInt32(itemCountOrdinal),
            reader.IsDBNull(itemSlotTypeOrdinal) ? null : (SlotType)reader.GetByte(itemSlotTypeOrdinal),
            reader.IsDBNull(itemSlotOrdinal) ? null : reader.GetByte(itemSlotOrdinal),
            reader.IsDBNull(moneyAmountOrdinal) ? null : reader.GetInt64(moneyAmountOrdinal));
    }

    private static void EnsureMatchingReceipt(
        AuctionMailClaimReceipt expected,
        AuctionMailClaimReceipt actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"Auction mail claim key {expected.MailId}/{expected.ClaimType} already has a different durable result.");
        }
    }

    private static void WriteMail(
        MySqlConnection connection,
        MySqlTransaction transaction,
        AuctionMailClaimPlan plan)
    {
        var mail = plan.Mail;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "REPLACE INTO `mails` (" +
            "`id`,`type`,`status`,`title`,`text`,`sender_id`,`sender_name`,`attachment_count`," +
            "`receiver_id`,`receiver_name`,`open_date`,`send_date`,`received_date`,`returned`,`extra`," +
            "`money_amount_1`,`money_amount_2`,`money_amount_3`," +
            "`attachment0`,`attachment1`,`attachment2`,`attachment3`,`attachment4`," +
            "`attachment5`,`attachment6`,`attachment7`,`attachment8`,`attachment9`) " +
            "VALUES (" +
            "@id,@type,@status,@title,@text,@sender_id,@sender_name,@attachment_count," +
            "@receiver_id,@receiver_name,@open_date,@send_date,@received_date,@returned,@extra," +
            "@money_amount_1,@money_amount_2,@money_amount_3," +
            "@attachment0,@attachment1,@attachment2,@attachment3,@attachment4," +
            "@attachment5,@attachment6,@attachment7,@attachment8,@attachment9)";
        command.Parameters.AddWithValue("@id", CheckedMailId(mail.Id));
        command.Parameters.AddWithValue("@type", (byte)mail.MailType);
        command.Parameters.AddWithValue("@status", (byte)plan.FinalMailStatus);
        command.Parameters.AddWithValue("@title", mail.Title);
        command.Parameters.AddWithValue("@text", mail.Body.Text);
        command.Parameters.AddWithValue("@sender_id", mail.Header.SenderId);
        command.Parameters.AddWithValue("@sender_name", mail.Header.SenderName);
        command.Parameters.AddWithValue("@attachment_count", plan.FinalAttachmentCount);
        command.Parameters.AddWithValue("@receiver_id", mail.Header.ReceiverId);
        command.Parameters.AddWithValue("@receiver_name", mail.ReceiverName);
        command.Parameters.AddWithValue("@open_date", plan.FinalOpenDate);
        command.Parameters.AddWithValue("@send_date", mail.Body.SendDate);
        command.Parameters.AddWithValue("@received_date", mail.Body.RecvDate);
        command.Parameters.AddWithValue("@returned", mail.Header.Returned ? 1 : 0);
        command.Parameters.AddWithValue("@extra", mail.Header.Extra);
        command.Parameters.AddWithValue(
            "@money_amount_1",
            plan is AuctionSaleClaimPlan ? 0 : mail.Body.CopperCoins);
        command.Parameters.AddWithValue("@money_amount_2", mail.Body.BillingAmount);
        command.Parameters.AddWithValue("@money_amount_3", mail.Body.MoneyAmount2);
        for (var index = 0; index < MailBody.MaxMailAttachments; index++)
        {
            command.Parameters.AddWithValue(
                $"@attachment{index}",
                index < plan.RemainingAttachments.Count
                    ? plan.RemainingAttachments[index].Id
                    : 0UL);
        }

        command.ExecuteNonQuery();
    }

    private static void WriteSaleState(
        MySqlConnection connection,
        MySqlTransaction transaction,
        AuctionSaleClaimPlan plan)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE `characters` SET `money` = @money, `level` = @level, `experience` = @experience, " +
                "`consumed_lp` = @consumed_lp, `updated_at` = @updated_at " +
                "WHERE `id` = @id AND `account_id` = @account_id";
            command.Parameters.AddWithValue("@money", plan.MoneyAfter);
            command.Parameters.AddWithValue("@level", plan.LevelAfter);
            command.Parameters.AddWithValue("@experience", plan.ExperienceAfter);
            command.Parameters.AddWithValue("@consumed_lp", plan.ConsumedLaborAfter);
            command.Parameters.AddWithValue("@updated_at", plan.UpdatedAt);
            command.Parameters.AddWithValue("@id", plan.Character.Id);
            command.Parameters.AddWithValue("@account_id", plan.Character.AccountId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The auction claimant character row was not updated.");
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE `accounts` SET `labor` = @labor WHERE `account_id` = @account_id";
            command.Parameters.AddWithValue("@labor", plan.LaborAfter);
            command.Parameters.AddWithValue("@account_id", plan.Character.AccountId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The auction claimant account labor row was not updated.");
        }

        foreach (var (abilityId, experience) in plan.AbilityExperienceAfter)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "REPLACE INTO `abilities` (`id`,`exp`,`owner`) VALUES (@id,@exp,@owner)";
            command.Parameters.AddWithValue("@id", (byte)abilityId);
            command.Parameters.AddWithValue("@exp", experience);
            command.Parameters.AddWithValue("@owner", plan.Character.Id);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "REPLACE INTO `actabilities` (`id`,`point`,`step`,`owner`) VALUES (@id,@point,@step,@owner)";
            command.Parameters.AddWithValue("@id", (byte)plan.ActabilityId);
            command.Parameters.AddWithValue("@point", plan.ActabilityPointAfter);
            command.Parameters.AddWithValue("@step", plan.ActabilityStep);
            command.Parameters.AddWithValue("@owner", plan.Character.Id);
            command.ExecuteNonQuery();
        }
    }

    private static void WriteBuyState(
        MySqlConnection connection,
        MySqlTransaction transaction,
        AuctionBuyClaimPlan plan)
    {
        var item = plan.DestinationStack ?? plan.SourceItem;
        WriteItem(
            connection,
            transaction,
            item,
            plan.DestinationCountAfter,
            plan.Character.Inventory.Bag.ContainerId,
            SlotType.Inventory,
            plan.DestinationSlot,
            plan.Character.Id,
            plan.FinalItemFlags);

        if (plan.DestinationStack == null)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM `items` WHERE `id` = @id";
        command.Parameters.AddWithValue("@id", plan.SourceItem.Id);
        command.ExecuteNonQuery();
    }

    private static void WriteItem(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Item item,
        int count,
        ulong containerId,
        SlotType slotType,
        int slot,
        uint ownerId,
        ItemFlag flags)
    {
        var details = new PacketStream();
        item.WriteDetails(details);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "REPLACE INTO `items` (" +
            "`id`,`type`,`template_id`,`container_id`,`slot_type`,`slot`,`count`,`details`,`lifespan_mins`,`made_unit_id`," +
            "`unsecure_time`,`unpack_time`,`owner`,`created_at`,`grade`,`flags`,`ucc`," +
            "`expire_time`,`expire_online_minutes`,`charge_time`,`charge_count`) " +
            "VALUES (" +
            "@id,@type,@template_id,@container_id,@slot_type,@slot,@count,@details,@lifespan_mins,@made_unit_id," +
            "@unsecure_time,@unpack_time,@owner,@created_at,@grade,@flags,@ucc," +
            "@expire_time,@expire_online_minutes,@charge_time,@charge_count)";
        command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@type", item.GetType().ToString());
        command.Parameters.AddWithValue("@template_id", item.TemplateId);
        command.Parameters.AddWithValue("@container_id", containerId);
        command.Parameters.AddWithValue("@slot_type", (int)slotType);
        command.Parameters.AddWithValue("@slot", slot);
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@details", details.GetBytes());
        command.Parameters.AddWithValue("@lifespan_mins", item.LifespanMins);
        command.Parameters.AddWithValue("@made_unit_id", item.MadeUnitId);
        command.Parameters.AddWithValue("@unsecure_time", item.UnsecureTime);
        command.Parameters.AddWithValue("@unpack_time", item.UnpackTime);
        command.Parameters.AddWithValue("@owner", ownerId);
        command.Parameters.AddWithValue("@created_at", item.CreateTime);
        command.Parameters.AddWithValue("@grade", item.Grade);
        command.Parameters.AddWithValue("@flags", (byte)flags);
        command.Parameters.AddWithValue("@ucc", item.UccId);
        command.Parameters.AddWithValue("@expire_time", item.ExpirationTime);
        command.Parameters.AddWithValue("@expire_online_minutes", item.ExpirationOnlineMinutesLeft);
        command.Parameters.AddWithValue("@charge_time", item.ChargeStartTime);
        command.Parameters.AddWithValue("@charge_count", item.ChargeCount);
        if (command.ExecuteNonQuery() < 1)
            throw new InvalidOperationException($"Auction claim item {item.Id} was not saved.");
    }

    private static void InsertReceipt(
        MySqlConnection connection,
        MySqlTransaction transaction,
        AuctionMailClaimReceipt receipt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO `auction_mail_claims` (" +
            "`mail_id`,`claim_type`,`receiver_id`,`item_id`,`item_count`,`item_slot_type`,`item_slot`,`money_amount`) " +
            "VALUES (@mail_id,@claim_type,@receiver_id,@item_id,@item_count,@item_slot_type,@item_slot,@money_amount)";
        command.Parameters.AddWithValue("@mail_id", CheckedMailId(receipt.MailId));
        command.Parameters.AddWithValue("@claim_type", (byte)receipt.ClaimType);
        command.Parameters.AddWithValue("@receiver_id", receipt.ReceiverId);
        command.Parameters.AddWithValue(
            "@item_id",
            receipt.ItemId.HasValue ? receipt.ItemId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "@item_count",
            receipt.ItemCount.HasValue ? receipt.ItemCount.Value : DBNull.Value);
        command.Parameters.AddWithValue("@item_slot_type", receipt.ItemSlotType is { } slotType
            ? (byte)slotType
            : DBNull.Value);
        command.Parameters.AddWithValue(
            "@item_slot",
            receipt.ItemSlot.HasValue ? receipt.ItemSlot.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "@money_amount",
            receipt.MoneyAmount.HasValue ? receipt.MoneyAmount.Value : DBNull.Value);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The auction mail claim receipt was not inserted.");
    }

    private static uint CheckedMailId(long mailId) => checked((uint)mailId);
}
