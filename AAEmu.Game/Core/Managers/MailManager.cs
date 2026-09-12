using System.Collections.Concurrent;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Tasks.Mails;
using MySql.Data.MySqlClient;
using NLog;

namespace AAEmu.Game.Core.Managers;

public partial class MailManager(IMailIdManager mailIdManager, INameManager nameManager, IItemManager itemManager, ITaskManager taskManager, IWorldManager worldManager, Lazy<IHousingManager> housingManager, ILocalizationManager localizationManager, Lazy<ISaveManager> saveManager = null) : Singleton<MailManager>, IMailManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public ConcurrentDictionary<long, BaseMail> _allPlayerMails;
    public IDictionary<long, BaseMail> AllPlayerMails => _allPlayerMails;
    private List<long> _deletedMailIds = [];
    // Unused: private object _lock = new();

    public static int CostNormal { get; set; } = 50;
    public static int CostNormalAttachment { get; set; } = 30;
    public static int CostExpress { get; set; } = 100;
    public static int CostExpressAttachment { get; set; } = 80;
    public static int CostFreeAttachmentCount { get; set; } = 1;
    public static TimeSpan NormalMailDelay { get; set; } = TimeSpan.FromMinutes(30); // Default is 30 minutes
    public static TimeSpan MailExpireDelay { get; set; } = TimeSpan.FromDays(14);

    public BaseMail GetMailById(long id)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_allPlayerMails.TryGetValue(id, out var theMail))
                return theMail;
            else
                return null;
        }
    }

    public uint GetNewMailId()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            lock (_deletedMailIds)
            {
                var Id = mailIdManager.GetNextId();
                if (_deletedMailIds.Contains(Id))
                    _deletedMailIds.Remove(Id);
                return Id;
            }
        }
    }

    internal void ReleaseReservedMailId(uint mailId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            mailIdManager.ReleaseId(mailId);
        }
    }

    public MailMutation BeginMutation() => new(this);

    internal uint ReserveMutationMailId() => mailIdManager.GetNextId();

    internal bool ValidateMutationMail(BaseMail mail)
    {
        if (mail?.Header == null || mail.Body == null || mail.Header.ReceiverId == 0 ||
            mail.Body.Attachments == null || mail.Body.Attachments.Count > MailBody.MaxMailAttachments ||
            !string.Equals(nameManager.GetCharacterName(mail.Header.ReceiverId), mail.ReceiverName,
                StringComparison.OrdinalIgnoreCase) ||
            nameManager.GetCharacterId(mail.ReceiverName) != mail.Header.ReceiverId)
            return false;

        var attachmentIds = new HashSet<ulong>();
        foreach (var item in mail.Body.Attachments)
        {
            var container = item?._holdingContainer;
            if (item == null || item.Id == 0 || item.Count <= 0 || !attachmentIds.Add(item.Id) ||
                item.SlotType != SlotType.Mail || item.OwnerId != mail.Header.ReceiverId ||
                container == null || container.ContainerType != SlotType.Mail ||
                container.OwnerId != mail.Header.ReceiverId || !container.Items.Contains(item) ||
                container.Items.Count(candidate => candidate.Id == item.Id) != 1 ||
                !ReferenceEquals(itemManager.GetItemByItemId(item.Id), item))
                return false;
        }
        return !_allPlayerMails.Values.Any(existing =>
            existing.Body.Attachments.Any(item => attachmentIds.Contains(item.Id)));
    }

    internal bool QueueMutationDeletion(long id)
    {
        lock (_deletedMailIds)
        {
            if (_deletedMailIds.Contains(id))
                return false;
            _deletedMailIds.Add(id);
            return true;
        }
    }

    internal void RestoreMutationDeletion(long id)
    {
        lock (_deletedMailIds)
            _deletedMailIds.Remove(id);
    }

    public bool Send(BaseMail mail)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            // Verify Receiver
            var targetName = nameManager.GetCharacterName(mail.Header.ReceiverId);
            var targetId = nameManager.GetCharacterId(mail.Header.ReceiverName);
            if (!string.Equals(targetName, mail.Header.ReceiverName, StringComparison.InvariantCultureIgnoreCase))
            {
                Logger.Debug("Send() - Failed to verify receiver name {0} != {1}", targetName, mail.Header.ReceiverName);
                return false; // Name mismatch
            }
            if (targetId != mail.Header.ReceiverId)
            {
                Logger.Debug("Send() - Failed to verify receiver id {0} != {1}", targetId, mail.Header.ReceiverId);
                return false; // Id mismatch
            }

            // Assign a Id if we didn't have one yet
            if (mail.Id <= 0)
            {
                Logger.Trace("Send() - Assign new mail Id");
                mail.Id = GetNewMailId();
            }
            if (!_allPlayerMails.TryAdd(mail.Id, mail))
                return false;
            NotifyNewMailByNameIfOnline(mail, targetName);
            return true;
        }
    }

    [Obsolete("SendMail() is deprecated. Use Send() of a BaseMail descendant instead.")]
    public void SendMail(MailType type, string receiverName, string senderName, string title, string text,
        byte attachments, int[] moneyAmounts, long extra, List<Item> items)
    {
        throw new GameException("SendMail is deprecated, use BaseMail.Send() instead");
    }

    public bool DeleteMail(long id)
    {
        if (!_allPlayerMails.TryGetValue(id, out var mail))
            return false;
        return DeleteMail(id, !IsAuctionClaimMail(mail));
    }

    private static bool IsAuctionClaimMail(BaseMail mail) =>
        mail.MailType is MailType.AucBidWin or MailType.AucOffSuccess;

    private bool DeleteMail(long id, bool releaseId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            lock (_deletedMailIds)
            {
                if (!_deletedMailIds.Contains(id))
                    _deletedMailIds.Add(id);
                if (releaseId)
                    mailIdManager.ReleaseId((uint)id);
            }
            return _allPlayerMails.TryRemove(id, out _);
        }
    }

    private bool DeleteTaxMail(long id)
    {
        // Keep paid/replaced tax mail IDs reserved for this server lifetime. A delayed replay of
        // the old client request must never resolve to the newly issued prepayment mail.
        return DeleteMail(id, false);
    }

    public bool DeleteMail(BaseMail mail, bool trashItems = false)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (trashItems)
            {
                for (var i = mail.Body.Attachments.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var item = mail.Body.Attachments[i];
                        item._holdingContainer.RemoveItem(ItemTaskType.Invalid, item, true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to remove mail attachment [{i}] from {mail.Id}: {ex}");
                    }
                }
            }
            return DeleteMail(mail.Id);
        }
    }

    #region Database
    public void Load()
    {
        Logger.Info("Loading player mails ...");
        _allPlayerMails = [];
        _deletedMailIds = [];

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM mails";
                command.Prepare();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tempMail = new BaseMail
                        {
                            Id = reader.GetInt32("id"), Title = reader.GetString("title"), MailType = (MailType)reader.GetInt32("type"),
                            ReceiverName = reader.GetString("receiver_name"),
                            OpenDate = reader.GetDateTime("open_date"),
                            Header =
                            {
                                Status = (MailStatus)reader.GetInt32("status"),
                                SenderId = reader.GetUInt32("sender_id"),
                                SenderName = reader.GetString("sender_name"),
                                Attachments = (byte)reader.GetInt32("attachment_count"),
                                ReceiverId = reader.GetUInt32("receiver_id"),
                                Returned = reader.GetInt32("returned") != 0,
                                Extra = reader.GetInt64("extra")
                            },
                            Body =
                            {
                                Text = reader.GetString("text"),
                                CopperCoins = reader.GetInt32("money_amount_1"),
                                BillingAmount = reader.GetInt32("money_amount_2"),
                                MoneyAmount2 = reader.GetInt32("money_amount_3"),
                                SendDate = reader.GetDateTime("send_date"),
                                RecvDate = reader.GetDateTime("received_date")
                            }
                        };

                        // Read/Load Items
                        tempMail.Body.Attachments.Clear();
                        for (var i = 0; i < MailBody.MaxMailAttachments; i++)
                        {
                            var itemId = reader.GetUInt64("attachment" + i.ToString());
                            if (itemId > 0)
                            {
                                var item = itemManager.GetItemByItemId(itemId);
                                if (item != null)
                                {
                                    // Keep persisted ownership for legacy auction attachments. Mail is not
                                    // authority to replace it, and an archive must not rewrite the original row.
                                    if (tempMail.MailType != MailType.AucBidWin ||
                                        item._holdingContainer?.ContainerType != SlotType.Auction)
                                        item.OwnerId = tempMail.Header.ReceiverId;
                                    tempMail.Body.Attachments.Add(item);
                                }
                                else
                                {
                                    tempMail.HasUnresolvedAttachments = true;
                                    Logger.Warn("Found orphaned itemId {0} in mailId {1}, not loaded!", itemId, tempMail.Id);
                                }
                            }
                        }
                        var attachmentCount = tempMail.Body.Attachments.Count;
                        if (tempMail.Body.CopperCoins > 0)
                            attachmentCount++;
                        if (tempMail.Body.BillingAmount > 0)
                            attachmentCount++;
                        if (tempMail.Body.MoneyAmount2 > 0)
                            attachmentCount++;
                        if (attachmentCount != tempMail.Header.Attachments)
                            Logger.Warn("Attachment count listed in mailId {0} did not match the number of attachments, possible mail or item corruption !", tempMail.Id);
                        // Reset the attachment counter
                        tempMail.Header.Attachments = (byte)attachmentCount;

                        // Set internal delivered flag
                        tempMail.IsDelivered = tempMail.Body.RecvDate <= DateTime.UtcNow;
                        tempMail.IsDirty = false;

                        // Remove from delete list if it's a recycled Id
                        if (_deletedMailIds.Contains(tempMail.Id))
                            _deletedMailIds.Remove(tempMail.Id);
                        _allPlayerMails.TryAdd(tempMail.Id, tempMail);
                    }
                }
            }
        }
        Logger.Info("Loaded {0} player mails", _allPlayerMails.Count);

        var mailCheckTask = new MailDeliveryTask();
        taskManager.Schedule(mailCheckTask, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
    }

    public (int, int) Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        return Save(connection, transaction, null);
    }

    public (int, int) Save(PersistenceSaveContext context)
    {
        return Save(context.Connection, context.Transaction, context);
    }

    private (int, int) Save(MySqlConnection connection, MySqlTransaction transaction, PersistenceSaveContext context)
    {
        var deletedCount = 0;
        var updatedCount = 0;
        // Logger.Info("Saving mail data ...");

        lock (_deletedMailIds)
        {
            deletedCount = _deletedMailIds.Count;
            if (_deletedMailIds.Count > 0)
            {
                var deletedIds = _deletedMailIds.ToArray();
                using (var command = connection.CreateCommand())
                {
                    command.Connection = connection;
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM mails WHERE `id` IN(" + string.Join(",", deletedIds) + ")";
                    command.Prepare();
                    command.ExecuteNonQuery();
                }
                void AcknowledgeDeletedMails()
                {
                    lock (_deletedMailIds)
                        foreach (var id in deletedIds)
                            _deletedMailIds.Remove(id);
                }
                if (context == null)
                    AcknowledgeDeletedMails();
                else
                    context.AfterCommit(AcknowledgeDeletedMails);
            }
        }

        foreach (var mtbs in _allPlayerMails)
        {
            // Preserve the original SQL references until the missing item is repaired.
            if (mtbs.Value.HasUnresolvedAttachments)
                continue;
            if (!mtbs.Value.IsDirty)
                continue;
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText = "REPLACE INTO mails(" +
                    "`id`,`type`,`status`,`title`,`text`,`sender_id`,`sender_name`," +
                    "`attachment_count`,`receiver_id`,`receiver_name`,`open_date`,`send_date`,`received_date`," +
                    "`returned`,`extra`,`money_amount_1`,`money_amount_2`,`money_amount_3`," +
                    "`attachment0`,`attachment1`,`attachment2`,`attachment3`,`attachment4`,`attachment5`," +
                    "`attachment6`,`attachment7`,`attachment8`,`attachment9`" +
                    ") VALUES (" +
                    "@id, @type, @status, @title, @text, @senderId, @senderName, " +
                    "@attachment_count, @receiverId, @receiverName, @openDate, @sendDate, @receivedDate, " +
                    "@returned, @extra, @money1, @money2, @money3," +
                    "@attachment0, @attachment1, @attachment2, @attachment3, @attachment4, @attachment5, " +
                    "@attachment6, @attachment7, @attachment8, @attachment9" +
                    ")";

                command.Parameters.AddWithValue("@id", mtbs.Value.Id);
                command.Parameters.AddWithValue("@openDate", mtbs.Value.Header.OpenDate);
                command.Parameters.AddWithValue("@type", (byte)mtbs.Value.Header.Type);
                command.Parameters.AddWithValue("@status", mtbs.Value.Header.Status);
                command.Parameters.AddWithValue("@title", mtbs.Value.Header.Title);
                command.Parameters.AddWithValue("@text", mtbs.Value.Body.Text);
                command.Parameters.AddWithValue("@senderId", mtbs.Value.Header.SenderId);
                command.Parameters.AddWithValue("@senderName", mtbs.Value.Header.SenderName);
                command.Parameters.AddWithValue("@attachment_count", mtbs.Value.Header.Attachments);
                command.Parameters.AddWithValue("@receiverId", mtbs.Value.Header.ReceiverId);
                command.Parameters.AddWithValue("@receiverName", mtbs.Value.Header.ReceiverName);
                command.Parameters.AddWithValue("@sendDate", mtbs.Value.Body.SendDate);
                command.Parameters.AddWithValue("@receivedDate", mtbs.Value.Body.RecvDate);
                command.Parameters.AddWithValue("@returned", mtbs.Value.Header.Returned ? 1 : 0);
                command.Parameters.AddWithValue("@extra", mtbs.Value.Header.Extra);
                command.Parameters.AddWithValue("@money1", mtbs.Value.Body.CopperCoins);
                command.Parameters.AddWithValue("@money2", mtbs.Value.Body.BillingAmount);
                command.Parameters.AddWithValue("@money3", mtbs.Value.Body.MoneyAmount2);

                for (var i = 0; i < MailBody.MaxMailAttachments; i++)
                {
                    if (i >= mtbs.Value.Body.Attachments.Count)
                        command.Parameters.AddWithValue("@attachment" + i.ToString(), 0);
                    else
                        command.Parameters.AddWithValue("@attachment" + i.ToString(), mtbs.Value.Body.Attachments[i].Id);
                }

                command.Prepare();
                command.ExecuteNonQuery();
                updatedCount++;
                if (context == null)
                    mtbs.Value.IsDirty = false;
                else
                    context.AfterCommit(() => mtbs.Value.IsDirty = false);
            }
        }

        return (updatedCount, deletedCount);
    }

    #endregion

    internal bool TryAddPlayerMail(BaseMail mail)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            return _allPlayerMails.TryAdd(mail.Id, mail);
        }
    }


    public Dictionary<long, BaseMail> GetCurrentMailList(uint characterId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var now = DateTime.UtcNow;
            // Try to grab the actual online Character object to send live updates
            var character = worldManager.GetCharacterById(characterId);
            var tempMails = _allPlayerMails.Where(
                x => x.Value.Body.RecvDate <= now &&
                     !IsExpired(x.Value, now) &&
                     (x.Value.Header.ReceiverId == characterId ||
                      x.Value.Header.SenderId == characterId)
                     ).
                ToDictionary(x => x.Key, x => x.Value);
            character?.Mails.UnreadMailCount.ResetReceived();
            foreach (var mail in tempMails)
            {
                //if ((mail.Value.Header.Status != MailStatus.Read) && (mail.Value.Header.SenderId != character.Id))
                if (mail.Value.Header.ReceiverId == characterId && mail.Value.Header.Status != MailStatus.Read)
                {
                    character?.Mails.UnreadMailCount.UpdateReceived(mail.Value.MailType, 1);
                    var addBody = mail.Value.MailType == MailType.Charged;

                    character?.SendPacket(new SCGotMailPacket(mail.Value.Header, character.Mails.UnreadMailCount, false, addBody ? mail.Value.Body : null));
                    mail.Value.IsDelivered = true;
                }
            }
            return tempMails;
        }
    }

    public bool NotifyNewMailByNameIfOnline(BaseMail m, string receiverName)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            Logger.Trace($"NotifyNewMailByNameIfOnline() - {receiverName}");
            // If unread and ready to deliver
            if (m.Header.Status != MailStatus.Read && m.Body.RecvDate <= DateTime.UtcNow && m.IsDelivered == false)
            {
                var player = worldManager.GetCharacter(receiverName);
                if (player != null)
                {
                    // TODO: Mia mail stuff
                    var addBody = m.MailType == MailType.Charged;
                    player.Mails.UnreadMailCount.UpdateReceived(m.MailType, 1);

                    player.SendPacket(new SCGotMailPacket(m.Header, player.Mails.UnreadMailCount, false, addBody ? m.Body : null));
                    m.IsDelivered = true;
                    return true;
                }
            }
            return false;
        }
    }

    public bool NotifyDeleteMailByNameIfOnline(BaseMail m, string receiverName)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            Logger.Trace($"NotifyDeleteMailByNameIfOnline() - {receiverName}");
            var player = worldManager.GetCharacter(receiverName);
            if (player != null)
            {
                if (m.Header.Status != MailStatus.Read)
                    player.Mails.UnreadMailCount.UpdateReceived(m.MailType, -1);
                player.SendPacket(new SCMailDeletedPacket(false, m.Id, true, player.Mails.UnreadMailCount));
                return true;
            }
            return false;
        }
    }

    public bool NotifyMailReceiverOpenedIfSenderOnline(BaseMail mail)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (mail.Header.SenderId == 0)
                return false;

            var sender = worldManager.GetCharacterById(mail.Header.SenderId);
            if (sender == null)
                return false;

            // The client applies this receipt on its next normal Sent-list refresh.
            // SCMailListEnd is not a redraw signal because it ends every active mail-list transfer.
            sender.SendPacket(new SCMailReceiverOpenedPacket(mail.Id, mail.OpenDate));
            return true;
        }
    }

    public bool NotifyMailRemovedIfSenderOnline(BaseMail mail)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (mail.Header.SenderId == 0)
                return false;

            var sender = worldManager.GetCharacterById(mail.Header.SenderId);
            if (sender == null)
                return false;

            sender.SendPacket(new SCMailRemovedPacket(true, mail.Id));
            return true;
        }
    }

    public void CheckAllMailTimings()
    {
        CheckAllMailTimings(DateTime.UtcNow);
    }

    internal void CheckAllMailTimings(DateTime now)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var mail in _allPlayerMails.Values.ToArray())
                if (IsExpired(mail, now))
                    TransitionMail(mail, null, true, now);

            // Deliver yet "undelivered" mails
            Logger.Trace("CheckAllMailTimings");
            var undeliveredMails = _allPlayerMails.Where(x => x.Value.Body.RecvDate <= now && !IsExpired(x.Value, now) && x.Value.IsDelivered == false).ToDictionary(x => x.Key, x => x.Value);
            var delivered = 0;
            foreach (var mail in undeliveredMails)
                if (NotifyNewMailByNameIfOnline(mail.Value, mail.Value.Header.ReceiverName))
                    delivered++;
            if (delivered > 0)
                Logger.Debug($"{delivered}/{undeliveredMails.Count} mail(s) delivered");

        }
    }

    public bool PayChargeMoney(Character character, long mailId, bool autoUseAAPoint)
    {
        // Character store state is always locked before the cross-manager save epoch.
        lock (character.StorePurchaseSyncRoot)
        lock (SaveManager.PersistenceSyncRoot)
            return PayChargeMoneyLocked(character, mailId, autoUseAAPoint);
    }

    private bool PayChargeMoneyLocked(Character character, long mailId, bool autoUseAAPoint)
    {
        var mail = GetMailById(mailId);
        if (mail == null)
        {
            character.SendErrorMessage(ErrorMessageType.MailInvalid);
            return false;
        }

        // Only server-authored house tax mail is supported.
        if (!MailForTax.IsTaxMail(mail))
        {
            character.SendErrorMessage(ErrorMessageType.MailInvalid);
            return false;
        }

        var houseId = (uint)(mail.Header.Extra & 0xFFFFFFFF); // Extract house DB Id from Extra
        var house = housingManager.Value.GetHouseById(houseId);

        if (house == null)
        {
            character.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
            return false;
        }

        lock (house.TaxPaymentSyncRoot)
        {
            // The mail may have been paid or reconciled by another request while this one waited for the house lock.
            if (!ReferenceEquals(GetMailById(mailId), mail))
            {
                character.SendErrorMessage(ErrorMessageType.MailInvalid);
                return false;
            }

            if (mail.Header.ReceiverId != character.Id ||
                !string.Equals(mail.ReceiverName, character.Name, StringComparison.OrdinalIgnoreCase) ||
                house.OwnerId != character.Id ||
                house.AccountId != character.AccountId)
            {
                character.SendErrorMessage(ErrorMessageType.MailInvalid);
                return false;
            }

            if (house.ProtectionEndDate <= DateTime.UtcNow)
            {
                DeleteHouseMails(house.Id);
                character.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
                return false;
            }

            if (!housingManager.Value.CanPayTaxMail(house))
            {
                DeleteHouseMails(house.Id);
                character.SendErrorMessage(ErrorMessageType.MailInvalid);
                return false;
            }

            var currentTaxAmount = housingManager.Value.GetWeeklyTaxAmount(house);
            if (!currentTaxAmount.HasValue)
            {
                character.SendErrorMessage(ErrorMessageType.InvalidTaxation);
                return false;
            }

            // Never charge a different amount from the one the player saw. Replace a stale quote and require a new click.
            if (mail.Body.BillingAmount != currentTaxAmount.Value)
            {
                DeleteHouseMails(house.Id);
                housingManager.Value.OfferTaxPrepayment(house);
                character.SendErrorMessage(ErrorMessageType.InvalidTaxation);
                return false;
            }

            var lateFeePercent = house.TaxDueDate <= DateTime.UtcNow ? AppConfiguration.Instance.World.HouseLateFeePercent : 0;
            var previousProtectionEndDate = house.ProtectionEndDate;
            var previousDirty = house.IsDirty;
            var certificates = FeaturesManager.Fsets.Check(Feature.taxItem);
            using var inventory = new InventoryMutation(ItemTaskType.Mail);
            using var mails = BeginMutation();
            if (!HousingManager.TryStageTaxPayment(inventory, character, currentTaxAmount.Value, certificates))
            {
                character.SendErrorMessage(ErrorMessageType.MailNotEnoughMoneyToPayTaxes);
                return false;
            }
            foreach (var oldBill in GetMyHouseMails(house.Id))
                if (!mails.TryRemove(oldBill))
                    return false;
            if (!housingManager.Value.PayWeeklyTax(house))
                return false;

            bool committed;
            try
            {
                committed = (saveManager?.Value ?? SaveManager.Instance).TryCommitEconomy([character], context =>
                {
                    if (!house.Save(context))
                        throw new InvalidOperationException("The paid house could not be saved.");
                    HouseTaxReceipt.Save(context, mail.Id, house, character.Id,
                        currentTaxAmount.Value, certificates, previousProtectionEndDate, DateTime.UtcNow, lateFeePercent);
                });
            }
            catch
            {
                // Commit outcome is unknown. Keep the prepared debit and deadline together.
                inventory.PreservePreparedState();
                mails.PreservePreparedState();
                throw;
            }
            if (!committed)
            {
                house.ProtectionEndDate = previousProtectionEndDate;
                house.IsDirty = previousDirty;
                character.SendErrorMessage(ErrorMessageType.MailUnknownFailure);
                return false;
            }
            try
            {
                inventory.Complete();
                character.SendPacket(new SCChargeMoneyPaidPacket(mail.Id));
                mails.Complete();
            }
            catch
            {
                inventory.PreservePreparedState();
                mails.PreservePreparedState();
                throw;
            }
            housingManager.Value.OfferTaxPrepayment(house);
            character.Mails.SendUnreadMailCount();
        }

        return true;
    }

    internal static bool TryConsumeTaxCertificates(
        int requiredCerts,
        int userBoundTaxCount,
        int userTaxCount,
        Func<int, int> consumeBoundCerts,
        Func<int, int> consumeTaxCerts,
        Func<int, bool> restoreBoundCerts,
        Func<int, bool> restoreTaxCerts,
        out bool fullyRestored)
    {
        fullyRestored = true;
        if (userBoundTaxCount + userTaxCount < requiredCerts)
            return false;

        var requestedBoundCerts = Math.Min(userBoundTaxCount, requiredCerts);
        var consumedBoundCerts = requestedBoundCerts > 0 ? consumeBoundCerts(requestedBoundCerts) : 0;
        var requestedTaxCerts = requiredCerts - consumedBoundCerts;
        var consumedTaxCerts = requestedTaxCerts > 0 ? consumeTaxCerts(requestedTaxCerts) : 0;

        if (consumedBoundCerts + consumedTaxCerts == requiredCerts)
            return true;

        // A concurrent inventory change can invalidate the count snapshot. Restore any
        // certificates actually removed so a failed payment never partially charges.
        var restoredBoundCerts = consumedBoundCerts == 0 || restoreBoundCerts(consumedBoundCerts);
        var restoredTaxCerts = consumedTaxCerts == 0 || restoreTaxCerts(consumedTaxCerts);
        fullyRestored = restoredBoundCerts && restoredTaxCerts;
        return false;
    }

    public static void ExtractExtraForHouse(long extra, out ushort zoneGroupId, out uint houseId)
    {
        houseId = (uint)(extra & 0xFFFFFFFF); // Extract house DB Id from Extra
        zoneGroupId = (ushort)((extra >> 48) & 0xFFFF); // Extract zone group Id from Extra
    }

    public void DeleteHouseMails(uint houseId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var deleteList = new List<long>();
            // Check which mails to remove
            foreach (var m in _allPlayerMails)
            {
                if (MailForTax.IsTaxMail(m.Value))
                {
                    ExtractExtraForHouse(m.Value.Header.Extra, out _, out var hId);
                    if (houseId == hId)
                    {
                        deleteList.Add(m.Value.Id);
                    }
                }
            }
            // Actually remove them by Id
            foreach (var d in deleteList)
            {
                var mail = GetMailById(d);
                NotifyDeleteMailByNameIfOnline(mail, mail.ReceiverName);
                DeleteTaxMail(mail.Id);
            }
        }
    }

    public List<BaseMail> GetMyHouseMails(uint houseId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var resultList = new List<BaseMail>();
            // Check which mails to remove
            foreach (var m in _allPlayerMails)
            {
                if (MailForTax.IsTaxMail(m.Value))
                {
                    ExtractExtraForHouse(m.Value.Header.Extra, out _, out var hId);
                    if (houseId == hId)
                    {
                        resultList.Add(m.Value);
                    }
                }
            }
            return resultList;
        }
    }

    public bool TryCreateQuestRewardMails(ICharacter character, Quest quest,
        List<ItemCreationDefinition> itemCreationDefinitions, out List<QuestRewardMail> mails)
    {
        mails = [];
        // Check every reference before allocating any item or mail.
        foreach (var definition in itemCreationDefinitions)
        {
            var template = itemManager.GetTemplate(definition.TemplateId);
            if (template == null || template.MaxCount <= 0 || definition.Count < 0)
            {
                Logger.Error("Cannot prepare quest reward mail: quest={QuestId}, item={ItemId}, count={Count}",
                    quest.TemplateId, definition.TemplateId, definition.Count);
                return false;
            }
        }

        var createdItems = new List<Item>();
        var rewardItems = new List<(Item Item, ItemCreationDefinition Reward)>();
        try
        {
            foreach (var definition in itemCreationDefinitions)
            {
                var template = itemManager.GetTemplate(definition.TemplateId);
                var grade = template.FixedGrade >= 0 && !template.Gradable
                    ? template.FixedGrade
                    : Math.Max(0, definition.GradeId < 0 ? template.FixedGrade : definition.GradeId);
                var acquired = character.Inventory.MailAttachments.AcquireDefaultItemEx(ItemTaskType.Invalid,
                    definition.TemplateId, definition.Count, grade, out var items, out _, 0,
                    notifyInventory: false);
                createdItems.AddRange(items);
                rewardItems.AddRange(items.Select(item => (item, definition)));
                if (!acquired)
                {
                    DiscardQuestRewardAttachments(createdItems);
                    return false;
                }
            }

            var questName = localizationManager.Get("quest_contexts", "name", quest.TemplateId, quest.TemplateId.ToString());
            foreach (var attachments in rewardItems.Chunk(10))
            {
                // Attachments already belong to the mail container; no inventory notifications or second move.
                var mail = new BaseMail
                {
                    Header = { SenderId = 0, SenderName = ".questReward", ReceiverId = character.Id },
                    ReceiverName = character.Name,
                    MailType = MailType.SysExpress,
                    Title = questName,
                    Body = { Text = $"Reward for quest {questName}." }
                };
                mail.Body.Attachments.AddRange(attachments.Select(attachment => attachment.Item));
                mails.Add(new QuestRewardMail(mail, attachments.Select(attachment => (attachment.Reward, attachment.Item.Count)).ToArray()));
            }
            return true;
        }
        catch
        {
            DiscardQuestRewardAttachments(createdItems);
            throw;
        }
    }

    public void DiscardUnsentQuestRewardMails(IEnumerable<BaseMail> mails)
    {
        foreach (var mail in mails)
        {
            // A successful send owns these attachments. Never delete or reuse its IDs.
            _allPlayerMails.TryGetValue(mail.Id, out var existing);
            var idInUse = existing != null;
            if (ReferenceEquals(existing, mail))
                continue;
            DiscardQuestRewardAttachments(mail.Body.Attachments);
            if (mail.Id > 0 && !idInUse)
                mailIdManager.ReleaseId(checked((uint)mail.Id));
        }
    }

    private static void DiscardQuestRewardAttachments(IEnumerable<Item> items)
    {
        foreach (var item in items.ToArray())
            item._holdingContainer?.RemoveItem(ItemTaskType.Invalid, item, true);
    }
}
