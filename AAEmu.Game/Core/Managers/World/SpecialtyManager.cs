using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Tasks.Specialty;
using AAEmu.Game.Utils;
using AAEmu.Game.Utils.DB;
using NLog;

namespace AAEmu.Game.Core.Managers.World;

public partial class SpecialtyManager(
    IItemManager itemManager,
    IMailManager mailManager,
    IZoneManager zoneManager,
    ITaskManager taskManager,
    Lazy<ISaveManager> saveManager) : Singleton<SpecialtyManager>, ISpecialtyManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, Specialty> _specialties;
    private Dictionary<uint, SpecialtyBundleItem> _specialtyBundleItems;
    private Dictionary<uint, SpecialtyNpc> _specialtyNpc;

    //                 itemId           bundleId
    private Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>> _specialtyBundleItemsMapped;
    private Dictionary<(uint Item, uint Zone), SpecialtyDemand> _demand = [];
    private Dictionary<(uint Origin, uint Destination), Specialty> _routes = [];
    private bool _initialized;

    public void Load()
    {
        _specialties = [];
        _specialtyBundleItems = [];
        _specialtyNpc = [];
        _routes = [];

        _specialtyBundleItemsMapped = [];
        _demand = [];

        Logger.Info("SpecialtyManager is loading...");

        SpecialtyDemand.Validate(AppConfiguration.Instance.Specialty);

        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM specialties";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new Specialty
                        {
                            Id = reader.GetUInt32("id"),
                            RowZoneGroupId = reader.GetUInt32("row_zone_group_id"),
                            ColZoneGroupId = reader.GetUInt32("col_zone_group_id"),
                            Ratio = reader.GetUInt32("ratio"),
                            Profit = reader.GetUInt32("profit"),
                            VendorExist = reader.GetBoolean("vendor_exist", true)
                        };
                        _specialties.Add(template.Id, template);
                        _routes.Add((template.RowZoneGroupId, template.ColZoneGroupId), template);
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM specialty_bundle_items";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SpecialtyBundleItem
                        {
                            Id = reader.GetUInt32("id"),
                            ItemId = reader.GetUInt32("item_id"),
                            SpecialtyBundleId = reader.GetUInt32("specialty_bundle_id"),
                            Profit = reader.GetUInt32("profit"),
                            Ratio = reader.GetUInt32("ratio")
                        };
                        _specialtyBundleItems.Add(template.Id, template);

                        if (!_specialtyBundleItemsMapped.ContainsKey(template.ItemId))
                            _specialtyBundleItemsMapped.Add(template.ItemId, []);

                        _specialtyBundleItemsMapped[template.ItemId].Add(template.SpecialtyBundleId, template);
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM specialty_npcs";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SpecialtyNpc
                        {
                            Id = reader.GetUInt32("id"),
                            Name = reader.GetString("name"),
                            NpcId = reader.GetUInt32("npc_id"),
                            SpecialtyBundleId = reader.GetUInt32("specialty_bundle_id")
                        };

                        _specialtyNpc.Add(template.NpcId, template);
                    }
                }
            }
        }

        using (var connection = MySQL.CreateConnection())
        {
            var utcNow = DateTime.UtcNow;
            foreach (var saved in SpecialtyDemandStore.Load(connection))
                _demand.Add((saved.ItemId, saved.ZoneGroupId), saved.Advance(utcNow, AppConfiguration.Instance.Specialty));
        }
        Logger.Info("SpecialtyManager loaded");
    }

    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        var interval = TimeSpan.FromMinutes(Math.Min(AppConfiguration.Instance.Specialty.RatioDecreaseTickMinutes,
            AppConfiguration.Instance.Specialty.RatioRegenTickMinutes));
        taskManager.Schedule(new SpecialtyRatioConsumeTask(), interval, interval);
    }

    public int GetRatioForSpecialty(Character player, uint itemId = 0)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var backpack = player.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (backpack == null || (itemId != 0 && itemId != backpack.TemplateId))
                return 0;
            var zoneGroupId = zoneManager.GetZoneByKey(player.Transform.ZoneId)?.GroupId ?? 0;
            return GetRatio(backpack.TemplateId, zoneGroupId, DateTime.UtcNow);
        }
    }

    public List<(uint, uint)> GetRatiosForTargetRoute(uint fromZoneGroupId, uint toZoneGroupId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var utcNow = DateTime.UtcNow;
            return itemManager.GetAllItems().Where(item => item.SpecialtyZoneId == fromZoneGroupId)
                .Select(item => (item.Id, (uint)GetRatio(item.Id, toZoneGroupId, utcNow))).ToList();
        }
    }

    private int GetRatio(uint itemId, uint zoneGroupId, DateTime utcNow) =>
        (int)decimal.Floor(GetDemand(itemId, zoneGroupId, utcNow).Ratio);

    private SpecialtyDemand GetDemand(uint itemId, uint zoneGroupId, DateTime utcNow)
    {
        var key = (itemId, zoneGroupId);
        if (!_demand.TryGetValue(key, out var demand))
            demand = SpecialtyDemand.Create(itemId, zoneGroupId, utcNow, AppConfiguration.Instance.Specialty);
        return demand.Advance(utcNow, AppConfiguration.Instance.Specialty);
    }

    internal ErrorMessageType TryQuote(Character player, uint npcObjId, out Item backpack, out Npc npc,
        out uint destination, out int basePrice)
    {
        backpack = player.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        npc = player.ParentWorld?.GetNpc(npcObjId);
        destination = 0;
        basePrice = 0;
        if (backpack?.Template is not BackpackTemplate template)
            return ErrorMessageType.StoreBackpackNogoods;
        if (npc?.Template is not { Specialty: true })
            return ErrorMessageType.InvalidTarget;
        if (!ServiceInteraction.CanReach(player, npc, 2.5f))
            return ErrorMessageType.TooFarAway;
        destination = zoneManager.GetZoneByKey(npc.Transform.ZoneId)?.GroupId ?? 0;
        if (destination == 0 || template.SpecialtyZoneId == 0)
            return ErrorMessageType.Invalid;

        uint ratio;
        uint profit;
        if (_specialtyNpc.TryGetValue(npc.TemplateId, out var trader))
        {
            if (!_specialtyBundleItemsMapped.TryGetValue(backpack.TemplateId, out var bundles) ||
                !bundles.TryGetValue(trader.SpecialtyBundleId, out var bundle))
                return ErrorMessageType.Invalid;
            ratio = bundle.Ratio;
            profit = bundle.Profit;
        }
        else
        {
            if (template.SpecialtyZoneId == destination)
                return ErrorMessageType.StoreCantSellSameZone;
            if (!template.NormalSpeciality || !_routes.TryGetValue((template.SpecialtyZoneId, destination), out var route))
                return ErrorMessageType.Invalid;
            ratio = route.Ratio;
            profit = route.Profit;
        }
        var refundMultiplier = itemManager.GetGradeTemplate(backpack.Grade)?.RefundMultiplier ?? 100;
        basePrice = SpecialtyPrice.BasePrice(profit, ratio, template.Refund, refundMultiplier);
        return basePrice > 0 ? ErrorMessageType.NoErrorMessage : ErrorMessageType.Invalid;
    }

    public void ConsumeRatio() => AdvanceDemand();
    public void RegenRatio() => AdvanceDemand();

    private void AdvanceDemand()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var utcNow = DateTime.UtcNow;
            var changes = _demand.Values.Select(demand => demand.Advance(utcNow, AppConfiguration.Instance.Specialty))
                .Where(demand => demand != _demand[(demand.ItemId, demand.ZoneGroupId)]).ToArray();
            if (changes.Length == 0)
                return;
            try
            {
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                foreach (var demand in changes)
                    SpecialtyDemandStore.Save(connection, transaction, demand);
                transaction.Commit();
                foreach (var demand in changes)
                    _demand[(demand.ItemId, demand.ZoneGroupId)] = demand;
            }
            catch (Exception exception)
            {
                // Keep the old deadlines. The next tick calculates the same elapsed change.
                Logger.Error(exception, "Could not save specialty demand.");
            }
        }
    }

    // Retained for the existing introductory test.
    public static int GetValueOfOne() => 1;
}
