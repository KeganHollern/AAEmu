using System.Collections.Concurrent;
using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Auction.Templates;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

public partial class AuctionManager(IItemManager itemManager, INameManager nameManager, IAuctionIdManager auctionIdManager, ILocalizationManager localizationManager, ITaskManager taskManager, IMailManager mailManager, Lazy<ISaveManager> saveManager) : Singleton<AuctionManager>, IAuctionManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public ConcurrentDictionary<ulong, AuctionLot> AuctionLots { get; } = [];
    private ConcurrentBag<long> DeletedAuctionItemIds { get; } = [];

    private static int MaxListingFee => 1000000; // 100g, 100 copper coins = 1 silver, 100 silver = 1 gold.

    public void CancelAuctionLot(Character player, ulong auctionId)
    {
        if (player == null)
            return;

        ErrorMessageType error;
        lock (SaveManager.PersistenceSyncRoot)
            error = TryCancelAuctionLot(player, auctionId);
        if (error != ErrorMessageType.NoErrorMessage)
            player.SendErrorMessage(error);
    }

    public void BidOnAuctionLot(Character player, uint auctioneerId, uint auctioneerId2, AuctionLot lot, AuctionBid bid)
    {
        if (player == null)
            return;

        ErrorMessageType error;
        lock (SaveManager.PersistenceSyncRoot)
            error = TryBidOnAuctionLot(player, lot, bid);
        if (error != ErrorMessageType.NoErrorMessage)
            player.SendErrorMessage(error);
    }

    public void GetBidAuctionLots(Character player, int page)
    {
        if (player == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
            GetBidAuctionLotsLocked(player, page);
    }

    private void GetBidAuctionLotsLocked(Character player, int page)
    {
        var searchedArticles = AuctionLots.Values.Where(lot => lot.BidderId == player.Id &&
            lot.Item != null && lot.EndTime > DateTime.UtcNow).ToList();
        if (searchedArticles.Count <= 0)
        {
            player.SendPacket(new SCAuctionSearchedPacket(0, 0, [], (short)ErrorMessageType.NoErrorMessage, DateTime.UtcNow));
            return;
        }

        var articles = SortArticles(searchedArticles, AuctionSearchSortKind.Default, AuctionSearchSortOrder.Asc).ToArray();
        var dividedLists = Helpers.SplitArray(articles, 9); // We split the array into arrays of 9 values each

        if (page < 0 || page >= dividedLists.Length) // Stops client DC when requesting an out-of-bounds page
        {
            Logger.Warn($"[AH-BIDS] {player.Name} requested an out-of-bounds page: {page}/{dividedLists.Length - 1}");
            player.SendPacket(new SCAuctionSearchedPacket(page, 0, [], (short)ErrorMessageType.NoErrorMessage, DateTime.UtcNow));
            return;
        }
        player.SendPacket(new SCAuctionSearchedPacket(page, dividedLists[page].Length, dividedLists[page].ToList(), (short)ErrorMessageType.NoErrorMessage, DateTime.UtcNow));
    }

    private AuctionLot GetCheapestAuctionLot(uint templateId)
    {
        var tempList = AuctionLots.Values.Where(lot => lot.Item?.TemplateId == templateId &&
            lot.EndTime > DateTime.UtcNow).ToList();
        if (tempList.Count <= 0)
        {
            return null;
        }

        tempList = tempList.OrderBy(x => x.DirectMoney).ToList();

        return tempList.First();
    }

    public void CheapestAuctionLot(Character player, uint templateId, byte itemGrade = 0)
    {
        if (player == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        {
            var cheapestItem = GetCheapestAuctionLot(templateId);
            player.SendPacket(new SCAuctionLowestPricePacket(templateId, itemGrade, cheapestItem?.DirectMoney ?? 0));
        }
    }

    public void AddAuctionLot(AuctionLot lot)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!AuctionLots.TryAdd(lot.Id, lot))
                Logger.Warn($"Unable to add Auction Lot with Id {lot.Id}, possible duplicate Id");
        }
    }

    public void UpdateAuctionHouse()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var expired = AuctionLots.Values.Where(lot => lot.EndTime <= DateTime.UtcNow).ToArray();
            foreach (var lot in expired)
            {
                if (!TryExpireAuctionLot(lot))
                    Logger.Warn($"Auction lot {lot.Id} could not be settled; its escrow remains pending.");
            }
        }
    }

    public AuctionLot CreateAuctionLot(uint playerId, string playerName, Item itemToList, int startPrice, int buyoutPrice, AuctionDuration duration, int minStack = 1, int maxStack = 1)
    {
        if (playerId == 0 || itemToList == null || !ValidPrices(startPrice, buyoutPrice, duration))
            return null;

        var now = DateTime.UtcNow;
        var timeLeft = 6 << (int)duration;

        var newAuctionLot = new AuctionLot
        {
            Id = auctionIdManager.GetNextId(), Duration = duration, Item = itemToList, EndTime = now.AddHours(timeLeft),
            WorldId = itemToList.WorldId,
            ClientId = playerId,
            ClientName = playerName,
            StartMoney = startPrice,
            DirectMoney = buyoutPrice,
            PostDate = now,
            //ChargePercent = 100, // added in 5+
            BidWorldId = 255,
            BidderId = 0,
            BidderName = "",
            BidMoney = 0,
            Extra = 0,
            //MinStack = minStack, // added in 5+
            //MaxStack = maxStack, // added in 5+
            IsDirty = true
        };

        return newAuctionLot;
    }

    public void Load()
    {
        try
        {
            AuctionLots.Clear();
            DeletedAuctionItemIds.Clear();

            using (var connection = MySQL.CreateConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM auction_house";
                    command.Prepare();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var auctionLot = new AuctionLot
                            {
                                Id = reader.GetUInt64("id"),
                                Duration = (AuctionDuration)reader.GetByte("duration"), // 8 is 6 hours, 9 is 12 hours, 10 is 24 hours, 11 is 48 hours
                                Item = itemManager.GetItemByItemId(reader.GetUInt32("item_id")),
                                PostDate = reader.GetDateTime("post_date"),
                                EndTime = reader.GetDateTime("end_time"),
                                WorldId = reader.GetByte("world_id"),
                                ClientId = reader.GetUInt32("client_id"),
                                ClientName = reader.GetString("client_name"),
                                StartMoney = reader.GetInt32("start_money"),
                                DirectMoney = reader.GetInt32("direct_money"),
                                //ChargePercent = reader.GetInt32("charge_percent"), // added in 5+
                                BidWorldId = (byte)reader.GetInt32("bid_world_id"),
                                BidderId = reader.GetUInt32("bidder_id"),
                                BidderName = reader.GetString("bidder_name"),
                                BidMoney = reader.GetInt32("bid_money"),
                                Extra = reader.GetInt32("extra"),
                                //MinStack = reader.GetInt32("min_stack"), // added in 5+
                                //MaxStack = reader.GetInt32("max_stack") // added in 5+
                            };

                            AddAuctionLot(auctionLot);
                        }
                    }
                }
            }
            var auctionTask = new AuctionHouseTask();
            taskManager.Schedule(auctionTask, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load auction data: {ex.Message}");
        }
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

        if (!DeletedAuctionItemIds.IsEmpty)
        {
            var deletedIds = DeletedAuctionItemIds.ToArray();
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM auction_house WHERE `id` IN(" + string.Join(",", deletedIds) + ")";
                command.Prepare();
                deletedCount = command.ExecuteNonQuery();
            }
            void AcknowledgeDeletedLots()
            {
                var pending = deletedIds.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
                var retained = new List<long>();
                while (DeletedAuctionItemIds.TryTake(out var id))
                {
                    if (pending.TryGetValue(id, out var count) && count > 0)
                        pending[id] = count - 1;
                    else
                        retained.Add(id);
                }
                foreach (var id in retained)
                    DeletedAuctionItemIds.Add(id);
            }
            if (context == null)
                AcknowledgeDeletedLots();
            else
                context.AfterCommit(AcknowledgeDeletedLots);
        }

        var dirtyItems = AuctionLots.Values.Where(c => c.IsDirty);
        foreach (var lot in dirtyItems)
        {
            if (lot.Item == null || lot.Item.Count <= 0 ||
                lot.Item.SlotType == SlotType.None || !Enum.IsDefined(lot.Item.SlotType))
                throw new InvalidOperationException($"Auction lot {lot.Id} has no persistent item.");

            using var command = connection.CreateCommand();
            command.Connection = connection;
            command.Transaction = transaction;
            command.CommandText = BuildInsertQuery();
            AddParametersToCommand(command, lot);
            command.Prepare();
            updatedCount += command.ExecuteNonQuery();
            if (context == null)
                lot.IsDirty = false;
            else
                context.AfterCommit(() => lot.IsDirty = false);
        }

        return (updatedCount, deletedCount);
    }

    private string BuildInsertQuery()
    {
        var sb = new StringBuilder();
        sb.Append("REPLACE INTO auction_house(");
        sb.Append("`id`, `duration`, `item_id`, `post_date`, `stack_size`, `end_time`, ");
        sb.Append("`world_id`, `client_id`, `client_name`, `start_money`, `direct_money`, ");
        sb.Append("`bid_world_id`, `bidder_id`, `bidder_name`, `bid_money`, `extra`");
        sb.Append(") VALUES (");
        sb.Append("@id, @duration, @item_id, @post_date, @stack_size, @end_time, ");
        sb.Append("@world_id, @client_id, @client_name, @start_money, @direct_money, ");
        sb.Append("@bid_world_id, @bidder_id, @bidder_name, @bid_money, @extra");
        sb.Append(" )");

        return sb.ToString();
    }

    private void AddParametersToCommand(MySqlCommand command, AuctionLot lot)
    {
        command.Parameters.AddWithValue("@id", lot.Id);
        command.Parameters.AddWithValue("@duration", (byte)lot.Duration);
        command.Parameters.AddWithValue("@item_id", lot.Item.Id);
        command.Parameters.AddWithValue("@post_date", lot.PostDate);
        command.Parameters.AddWithValue("@stack_size", lot.Item.Count);
        command.Parameters.AddWithValue("@end_time", lot.EndTime);
        command.Parameters.AddWithValue("@world_id", lot.WorldId);
        command.Parameters.AddWithValue("@client_id", lot.ClientId);
        command.Parameters.AddWithValue("@client_name", lot.ClientName);
        command.Parameters.AddWithValue("@start_money", lot.StartMoney);
        command.Parameters.AddWithValue("@direct_money", lot.DirectMoney);
        //command.Parameters.AddWithValue("@charge_percent", lot.ChargePercent); // added in 5+
        command.Parameters.AddWithValue("@bid_world_id", lot.BidWorldId);
        command.Parameters.AddWithValue("@bidder_id", lot.BidderId);
        command.Parameters.AddWithValue("@bidder_name", lot.BidderName);
        command.Parameters.AddWithValue("@bid_money", lot.BidMoney);
        command.Parameters.AddWithValue("@extra", lot.Extra);
        //command.Parameters.AddWithValue("@min_stack", lot.MinStack); // added in 5+
        //command.Parameters.AddWithValue("@max_stack", lot.MaxStack); // added in 5+
    }

    private List<AuctionLot> SortArticles(List<AuctionLot> articles, AuctionSearchSortKind kind, AuctionSearchSortOrder order)
    {
        var sortedArticles = articles.AsQueryable();

        switch (kind)
        {
            case AuctionSearchSortKind.BidPrice:
                sortedArticles = order == AuctionSearchSortOrder.Asc
                    ? sortedArticles.OrderBy(o => o.BidMoney)
                    : sortedArticles.OrderByDescending(o => o.BidMoney);
                break;
            case AuctionSearchSortKind.DirectPrice:
                sortedArticles = order == AuctionSearchSortOrder.Asc
                    ? sortedArticles.OrderBy(o => o.DirectMoney)
                    : sortedArticles.OrderByDescending(o => o.DirectMoney);
                break;
            case AuctionSearchSortKind.ExpireDate:
                sortedArticles = order == AuctionSearchSortOrder.Asc
                    ? sortedArticles.OrderBy(o => o.PostDate)
                    : sortedArticles.OrderByDescending(o => o.PostDate);
                break;
            case AuctionSearchSortKind.ItemLevel:
                sortedArticles = order == AuctionSearchSortOrder.Asc
                    ? sortedArticles.OrderBy(o => o.Item.Template.Level)
                    : sortedArticles.OrderByDescending(o => o.Item.Template.Level);
                break;
        }

        return sortedArticles.ToList();
    }

    public void SearchAuctionLots(Character player, AuctionSearch search)
    {
        if (player == null || search == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
            SearchAuctionLotsLocked(player, search);
    }

    private void SearchAuctionLotsLocked(Character player, AuctionSearch search)
    {
        var searchedArticles = new List<AuctionLot>();

        foreach (var (_, lot) in AuctionLots)
        {
            if (lot.Item?.Template == null || lot.EndTime <= DateTime.UtcNow)
                continue;
            var template = lot.Item.Template;
            var settings = template.AuctionSettings;

            // Check by ClientId
            if (search.ClientId != 0 && lot.ClientId != search.ClientId)
            {
                continue;
            }

            // Check keyword
            if (!string.IsNullOrWhiteSpace(search.Keyword))
            {
                if (!localizationManager.MatchItemName(template.Id, search.Keyword, search.ExactMatch))
                    continue;
            }

            // Check by category and other criteria
            if (settings.CategoryA != search.CategoryA && search.CategoryA != 0)
            {
                continue;
            }

            if (settings.CategoryB != search.CategoryB && search.CategoryB != 0)
            {
                continue;
            }

            if (settings.CategoryC != search.CategoryC && search.CategoryC != 0)
            {
                continue;
            }

            if (lot.Item.Grade != search.Grade && search.Grade != 0)
            {
                continue;
            }

            if (template.Level > search.MaxItemLevel && search.MaxItemLevel != 0)
            {
                continue;
            }

            if (template.Level < search.MinItemLevel && search.MinItemLevel != 0)
            {
                continue;
            }

            searchedArticles.Add(lot);
        }

        if (searchedArticles.Count == 0)
        {
            player.SendPacket(new SCAuctionSearchedPacket(0, 0, [], (short)ErrorMessageType.NoErrorMessage, DateTime.UtcNow));
            return;
        }

        var articles = SortArticles(searchedArticles, search.SortKind, search.SortOrder).ToArray();
        var dividedLists = Helpers.SplitArray(articles, 9); // Разделяем массив на массивы по 9 значений

        if (search.Page < 0 || search.Page >= dividedLists.Length) // Stops client DC when requesting an out-of-bounds page
        {
            Logger.Warn($"[AH] {player.Name} requested an out-of-bounds page: {search.Page}/{dividedLists.Length - 1}");
            player.SendPacket(new SCAuctionSearchedPacket(search.Page, 0, [], (short)ErrorMessageType.NoErrorMessage, DateTime.UtcNow));
            return;
        }
        player.SendPacket(new SCAuctionSearchedPacket(search.Page, dividedLists[search.Page].Length, dividedLists[search.Page].ToList(), (short)ErrorMessageType.NoErrorMessage, DateTime.UtcNow));
    }

    public void PostLotOnAuction(Character player, uint npcId, uint npcId2, ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration)
    {
        if (player == null)
            return;

        ErrorMessageType error;
        lock (SaveManager.PersistenceSyncRoot)
            error = TryPostLotOnAuction(player, itemId, startPrice, buyoutPrice, duration);
        if (error != ErrorMessageType.NoErrorMessage)
            player.SendErrorMessage(error);
    }
}
