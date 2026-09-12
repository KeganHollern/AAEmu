using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Housing;
using AAEmu.Game.Utils;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

public partial class HousingManager(
    IObjectIdManager objectIdManager,
    IFactionManager factionManager,
    ILocalizationManager localizationManager,
    IWorldManager worldManager,
    ITaskManager taskManager,
    ISkillManager skillManager,
    IHousingIdManager housingIdManager,
    IHousingTldManager housingTldManager,
    IItemManager itemManager,
    IMailManager mailManager,
    INameManager nameManager,
    IZoneManager zoneManager,
    IDoodadManager doodadManager,
    IUccManager uccManager,
    Lazy<ISaveManager> saveManager = null,
    IDoodadIdManager decorationIdManager = null) : Singleton<HousingManager>, IHousingManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const uint ForSaleMarkerDoodadId = 6760;
    private const int MaxHeavyTaxCounted = 10; // Maximum number of heavy tax buildings to take into account for tax calculation
    private const int HoursForFailedTaxToReturnHouse = 22;
    private const double CopperPerCertificate = 1000000.0; // For older versions of AA, 1 sale certificate / 100g
    private Dictionary<uint, House> _houses = [];
    private Dictionary<ushort, House> _housesTl = []; // TODO or so mb tlId is id in the active zone? or type of house
    private List<uint> _removedHousings = [];
    private bool _isCheckingTaxTiming;

    /// <summary>
    /// Gets all houses for a given Account
    /// </summary>
    /// <param name="values"></param>
    /// <param name="accountId"></param>
    /// <returns></returns>
    public int GetByAccountId(Dictionary<uint, House> values, uint accountId)
    {
        foreach (var (id, house) in _houses)
            if (house.AccountId == accountId)
                values.Add(id, house);
        return values.Count;
    }

    /// <summary>
    /// Gets all houses owned by Character
    /// </summary>
    /// <param name="values"></param>
    /// <param name="characterId"></param>
    /// <returns></returns>
    public int GetByCharacterId(Dictionary<uint, House> values, uint characterId)
    {
        foreach (var (id, house) in _houses)
            if (house.OwnerId == characterId)
                values.Add(id, house);
        return values.Count;
    }

    /// <summary>
    /// Creates House and set it's untouchable buff
    /// </summary>
    /// <param name="templateId"></param>
    /// <param name="factionId"></param>
    /// <param name="worldInstance"></param>
    /// <param name="objectId"></param>
    /// <param name="tlId"></param>
    /// <returns></returns>
    private House Create(uint templateId, FactionsEnum factionId, WorldInstance worldInstance, uint objectId = 0, ushort tlId = 0)
    {
        var template = HousingGameData.Instance.GetTemplate(templateId);
        if (template == null)
            return null;

        var house = new House
        {
            TlId = tlId > 0 ? tlId : (ushort)housingTldManager.GetNextId(),
            ObjId = objectId > 0 ? objectId : objectIdManager.GetNextId(),
            Template = template,
            TemplateId = template.Id, // duplicate Id
            Id = template.Id,
            Faction = factionManager.GetFaction(factionId),
            Name = localizationManager.Get("housings", "name", template.Id),
            Transform = { InstanceId = worldInstance.Id }
        };
        house.Hp = house.MaxHp;
        // Force public on always public properties on create
        if (template.AlwaysPublic)
            house.Permission = HousingPermission.Public;

        SetUntouchable(house, true);

        return house;
    }

    /// <summary>
    /// Load housing definitions, player houses and starts tax check timer
    /// </summary>
    /// <exception cref="IOException"></exception>
    public void LoadPlayerHousing(WorldInstance worldInstance)
    {
        _houses = [];
        _housesTl = [];
        _removedHousings = [];

        worldInstance ??= worldManager.GetWorld(WorldManager.DefaultInstanceId);

        // var housingAreas = new Dictionary<uint, HousingAreas>();
        // var houseTaxes = new Dictionary<uint, HouseTax>();

        Logger.Info("Loading Player Buildings ...");
        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.CommandText = "SELECT * FROM housings";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var templateId = reader.GetUInt32("template_id");
                        var factionId = (FactionsEnum)reader.GetUInt32("faction_id");
                        var house = Create(templateId, factionId, worldInstance);
                        house.ParentWorld = worldInstance;
                        house.Id = reader.GetUInt32("id");
                        house.AccountId = reader.GetUInt32("account_id");
                        house.OwnerId = reader.GetUInt32("owner");
                        house.CoOwnerId = reader.GetUInt32("co_owner");
                        house.Name = reader.GetString("name");
                        house.Transform = new Transform(house, null,
                            new Vector3(reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z")),
                            new Vector3(reader.GetFloat("roll"), reader.GetFloat("pitch"), reader.GetFloat("yaw"))
                        );
                        house.Transform.InstanceId = house.ParentWorld.Id; // Just to be sure
                        house.Transform.ZoneId = worldManager.GetZoneId(house.ParentWorld.Template, house.Transform.World.Position.X, house.Transform.World.Position.Y);
                        house.IsBeingLoadedFromDb = AppConfiguration.Instance.World.UsePersistentHouseDoodads;
                        try
                        {
                            house.CurrentStep = reader.GetInt32("current_step");
                        }
                        finally
                        {
                            house.IsBeingLoadedFromDb = false;
                        }
                        house.NumAction = reader.GetInt32("current_action");
                        house.Permission = (HousingPermission)reader.GetByte("permission");
                        house.PlaceDate = reader.GetDateTime("place_date");
                        house.ProtectionEndDate = reader.GetDateTime("protected_until");
                        house.SellToPlayerId = reader.GetUInt32("sell_to");
                        house.SellPrice = reader.GetUInt32("sell_price");
                        house.AllowRecover = reader.GetBoolean("allow_recover");
                        _houses.Add(house.Id, house);
                        _housesTl.Add(house.TlId, house);

                        // Manually placed houses (or after upgrading MySQL), will get 2 weeks for free as to not immediately trigger them into demolition
                        if (house.PlaceDate == house.ProtectionEndDate)
                            house.ProtectionEndDate = house.PlaceDate.AddDays(14);

                        UpdateTaxInfo(house);
                        house.IsDirty = false;
                    }
                }
            }
        }

        Logger.Info($"Loaded {_houses.Count} Player Buildings");

        var houseCheckTask = new HousingTaxTask();
        taskManager.Schedule(houseCheckTask, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        Logger.Info("Started Housing Tax Timer");
    }

    /// <summary>
    /// Saves player housing information
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <returns></returns>
    public (int, int) Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        var deleteCount = 0;
        lock (_removedHousings)
        {
            if (_removedHousings.Count > 0)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"DELETE FROM housings WHERE id IN({string.Join(",", _removedHousings)})";
                    command.Prepare();
                    command.ExecuteNonQuery();
                    deleteCount++;
                }

                _removedHousings.Clear();
            }
        }

        var updateCount = 0;
        foreach (var house in _houses.Values)
            if (house.Save(connection, transaction))
                updateCount++;

        return (updateCount, deleteCount);
    }

    /// <summary>
    /// Spawn all houses
    /// </summary>
    public void SpawnAll()
    {
        foreach (var house in _houses.Values)
        {
            // Override instanceId to always be "main_world" instance
            house.Transform.InstanceId = WorldManager.DefaultInstanceId;
            house.Spawn();
        }
    }

    /// <summary>
    /// After persistent housing doodads have been loaded from DB, reconcile bound doodads for each completed house.
    /// Spawns and saves any bound doodads missing from the DB (first-run migration), and removes duplicates.
    /// </summary>
    public void ReconcileBoundDoodads()
    {
        Logger.Info("Reconciling bound doodads for completed houses...");
        var addedCount = 0;
        var removedCount = 0;

        foreach (var house in _houses.Values)
        {
            if (house.CurrentStep != -1)
                continue;
            if (house.Template.HousingBindingDoodad == null || house.Template.HousingBindingDoodad.Length == 0)
                continue;

            foreach (var bindingDoodad in house.Template.HousingBindingDoodad)
            {
                var matches = house.AttachedDoodads
                    .Where(d => d.TemplateId == bindingDoodad.DoodadId
                             && d.AttachPoint == bindingDoodad.AttachPointId)
                    .ToList();

                if (matches.Count == 0)
                {
                    // Missing from DB — spawn fresh and save (first-run migration or data loss recovery)
                    Logger.Debug($"Reconcile: Spawning missing bound doodad templateId={bindingDoodad.DoodadId} attachPoint={bindingDoodad.AttachPointId} for house {house.Id}");
                    var doodad = doodadManager.Create(house.ParentWorld, 0, bindingDoodad.DoodadId, house, true);
                    if (doodad == null)
                    {
                        Logger.Error($"Reconcile: Failed to create doodad templateId={bindingDoodad.DoodadId} for house {house.Id} — template not found, skipping.");
                        continue;
                    }
                    doodad.AttachPoint = bindingDoodad.AttachPointId;
                    doodad.ParentObj = house;
                    doodad.Transform = house.Transform.CloneDetached(doodad);
                    doodad.Transform.Parent = house.Transform;
                    doodad.Transform.Local.ApplyWorldSpawnPositionWithDeg(bindingDoodad.Position);
                    doodad.IsPersistent = true;
                    doodad.InitDoodad();
                    doodad.Spawn(); // register in world and make visible
                    doodad.Save();
                    house.AttachedDoodads.Add(doodad);
                    house.ParentWorld.SpawnManager.AddPlayerDoodad(doodad);
                    addedCount++;
                }
                else if (matches.Count > 1)
                {
                    // Duplicates — keep the first (earliest loaded = lowest DbId), delete extras
                    Logger.Warn($"Reconcile: Removing {matches.Count - 1} duplicate(s) for templateId={bindingDoodad.DoodadId} attachPoint={bindingDoodad.AttachPointId} on house {house.Id}");
                    for (var i = 1; i < matches.Count; i++)
                    {
                        var extra = matches[i];
                        house.AttachedDoodads.Remove(extra);
                        if (extra.ObjId > 0)
                            ObjectIdManager.Instance.ReleaseId(extra.ObjId);
                        extra.Delete();
                        removedCount++;
                    }
                }
            }
        }

        Logger.Info($"Bound doodad reconciliation complete: {addedCount} added, {removedCount} duplicates removed.");
    }

    /// <summary>
    /// Sets or removes the untouchable buff for the house
    /// </summary>
    /// <param name="house"></param>
    /// <param name="isUntouchable"></param>
    private void SetUntouchable(House house, bool isUntouchable)
    {
        if (isUntouchable)
        {
            if (house.Buffs.CheckBuff((uint)BuffConstants.Untouchable))
                return;

            // Permanent Untouchable buff, should only be removed when failed tax payment, or demolishing by hand
            var protectionBuffTemplate = skillManager.GetBuffTemplate((uint)BuffConstants.Untouchable);
            if (protectionBuffTemplate != null)
            {
                var casterObj = new SkillCasterUnit(house.ObjId);
                house.Buffs.AddBuff(new Buff(house, house, casterObj,
                    protectionBuffTemplate, null, DateTime.UtcNow));
            }
            else
            {
                Logger.Error("Unable to find Untouchable buff template");
            }
        }
        else
        {
            // Remove Untouchable if it's enabled
            if (house.Buffs.CheckBuff((uint)BuffConstants.Untouchable))
                house.Buffs.RemoveBuff((uint)BuffConstants.Untouchable);
        }
    }

    /// <summary>
    /// Sets or removes the removal debuff for demolishing houses
    /// </summary>
    /// <param name="house"></param>
    /// <param name="isDeteriorating"></param>
    private void SetRemovalDebuff(House house, bool isDeteriorating)
    {
        if (isDeteriorating)
        {
            if (!house.Buffs.CheckBuff((uint)BuffConstants.RemovalDebuff))
            {
                // Permanent Untouchable buff, should only be removed when failed tax payment, or demolishing by hand
                var protectionBuffTemplate = skillManager.GetBuffTemplate((uint)BuffConstants.RemovalDebuff);
                if (protectionBuffTemplate != null)
                {
                    var casterObj = new SkillCasterUnit(house.ObjId);
                    house.Buffs.AddBuff(new Buff(house, house, casterObj,
                        protectionBuffTemplate, null, DateTime.UtcNow));
                }
                else
                {
                    Logger.Error("Unable to find Removal Debuff template");
                }
            }
        }
        else
        {
            // Remove Untouchable if it's enabled
            if (house.Buffs.CheckBuff((uint)BuffConstants.RemovalDebuff))
                house.Buffs.RemoveBuff((uint)BuffConstants.RemovalDebuff);
        }
    }

    /// <summary>
    /// Sends tax information about a house
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="designId"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    public void ConstructHouseTax(GameConnection connection, uint designId, float x, float y, float z)
    {
        // TODO validation position and some range...

        var houseTemplate = HousingGameData.Instance.GetTemplate(designId);

        CalculateBuildingTaxInfo(connection.ActiveChar.AccountId, houseTemplate, true, out var totalTaxAmountDue, out var heavyTaxHouseCount, out var normalTaxHouseCount, out _, out _);

        var baseTax = (int)(houseTemplate.Taxation?.Tax ?? 0);
        var depositTax = baseTax * 2;

        connection.SendPacket(
            new SCConstructHouseTaxPacket(designId,
                heavyTaxHouseCount,
                normalTaxHouseCount,
                houseTemplate.HeavyTax,
                baseTax,
                depositTax,
                totalTaxAmountDue
            )
        );
    }

    /// <summary>
    /// Request house tax information (using name plaque of a house)
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    public void HouseTaxInfo(GameConnection connection, ushort tlId)
    {
        if (!_housesTl.TryGetValue(tlId, out var house))
            return;

        CalculateBuildingTaxInfo(house.AccountId, house.Template, false, out var totalTaxAmountDue, out _, out _, out _, out _);

        var baseTax = (int)(house.Template.Taxation?.Tax ?? 0);
        var depositTax = baseTax * 2;

        var utcNow = DateTime.UtcNow;
        totalTaxAmountDue = HouseTaxAmount.WithLateFee(totalTaxAmountDue, house.TaxDueDate, utcNow,
            AppConfiguration.Instance.World.HouseLateFeePercent);
        var isAlreadyPaid = house.TaxDueDate > utcNow;
        var weeksWithoutPay = isAlreadyPaid ? 0 : 1;

        connection.SendPacket(
            new SCHouseTaxInfoPacket(
                house.TlId,
                0,  // TODO: implement when castles are added
                depositTax, // this is used in the help text on (?) when you hover your mouse over it to display deposit tax for this building
                totalTaxAmountDue, // Amount Due
                house.ProtectionEndDate,
                isAlreadyPaid,
                weeksWithoutPay,  // TODO: do proper calculation ?
                house.Template.HeavyTax
            )
        );
    }

    /// <summary>
    /// Start building a house at target location using design
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="designId"></param>
    /// <param name="posX"></param>
    /// <param name="posY"></param>
    /// <param name="posZ"></param>
    /// <param name="zRot"></param>
    /// <param name="itemId"></param>
    /// <param name="moneyAmount"></param>
    /// <param name="ht"></param>
    /// <param name="autoUseAaPoint"></param>
    public void Build(GameConnection connection, uint designId, float posX, float posY, float posZ, float zRot,
        ulong itemId, int moneyAmount, int ht, bool autoUseAaPoint)
    {
        // Serialize account-wide eligibility with house creation, sales and removal.
        lock (SaveManager.PersistenceSyncRoot)
        {
            BuildLocked(connection, designId, posX, posY, posZ, zRot, itemId);
        }
    }

    private void BuildLocked(GameConnection connection, uint designId, float posX, float posY, float posZ,
        float zRot, ulong itemId)
    {
        if (connection?.ActiveChar == null)
            return;

        var sourceDesignItem = connection.ActiveChar.Inventory.Bag.GetItemByItemId(itemId);
        var houseTemplate = sourceDesignItem == null
            ? null : HousingGameData.Instance.GetTemplateForItem(sourceDesignItem.TemplateId);
        if (sourceDesignItem == null || sourceDesignItem.OwnerId != connection.ActiveChar.Id ||
            sourceDesignItem.SlotType != SlotType.Inventory || sourceDesignItem.Count < 1 ||
            houseTemplate == null || houseTemplate.Id != designId)
        {
            // Invalid itemId supplied or the id is not owned by the user
            connection.ActiveChar.SendErrorMessage(ErrorMessageType.BagInvalidItem);
            return;
        }

        var placementError = !float.IsFinite(zRot)
            ? ErrorMessageType.HouseCannotLoacateInvalidCategoryArea
            : HousingPlacementRules.Check(HousingAreaGameData.Instance, connection.ActiveChar.ParentWorld?.Template,
                new Vector3(posX, posY, posZ), houseTemplate.CategoryId, connection.ActiveChar.AccountId, _houses.Values);
        if (placementError != ErrorMessageType.NoErrorMessage)
        {
            connection.ActiveChar.SendErrorMessage(placementError);
            return;
        }
        if (!CalculateBuildingTaxInfo(connection.ActiveChar.AccountId, houseTemplate, true,
                out var totalTaxAmountDue, out _, out _, out _, out _))
        {
            connection.ActiveChar.SendErrorMessage(ErrorMessageType.InvalidTaxation);
            return;
        }

        using var inventory = new InventoryMutation(ItemTaskType.HouseCreation);
        if (!TryStageCreationPayment(inventory, connection.ActiveChar, sourceDesignItem, totalTaxAmountDue,
                FeaturesManager.Fsets.Check(Models.Game.Features.Feature.taxItem)))
        {
            connection.ActiveChar.SendErrorMessage(ErrorMessageType.MailNotEnoughMoneyToPayTaxes);
            return;
        }

        // Spawn the actual house
        var house = Create(designId, connection.ActiveChar.Faction.Id, connection.ActiveChar.ParentWorld);

        // Fallback for un-translated buildings (en_us)
        if (house.Name == string.Empty)
        {
            var fakeLocalizedName = localizationManager.Get("items", "name", sourceDesignItem.Template.Id, houseTemplate.Name);
            if (fakeLocalizedName.EndsWith(" Design"))
                fakeLocalizedName = fakeLocalizedName.Replace(" Design", "");
            house.Name = fakeLocalizedName;
        }

        house.Id = housingIdManager.GetNextId();
        house.Transform.Local.SetPosition(posX, posY, posZ);
        // In 1.2 the rotation in SCUnitStatePacket is sent as X, Y, Z using 1 byte each.
        // This limits us to 256 unique rotations around Z (up) that can be represented.
        // When placing the house with the preview and then finalizing it, this causes the actual rotation to be different from the preview.
        // 3.0 sends a full 32-bit float for the Z-rotation for BaseUnitType.Housing, so this seems to have been fixed in later versions.
        // The fact the server has a more accurate view of the rotation than the client means positions of objects (doodads) placed in the house
        // can be offset.
        // To make the server and client agree on the rotation, we convert the float zRot to a sbyte, then back to a float.
        // The server then knows the rotation as one of the 256 unique rotations that the client can be sent.
        var (_, _, yaw) = PositionAndRotation.ToRollPitchYawSBytes(new Vector3(0, 0, zRot));
        zRot = PositionAndRotation.FromRollPitchYawSBytes(0, 0, yaw).Z;
        house.Transform.Local.SetRotation(0, 0, zRot);

        if (house.Template.BuildSteps.Count > 0)
            house.CurrentStep = 0;
        else
            house.CurrentStep = -1;
        house.OwnerId = connection.ActiveChar.Id;
        house.CoOwnerId = connection.ActiveChar.Id;
        house.AccountId = connection.AccountId;
        house.Permission = HousingPermission.Private;
        house.AllowRecover = true;
        SetInitialTaxDates(house, DateTime.UtcNow);
        _houses.Add(house.Id, house);
        _housesTl.Add(house.TlId, house);
        bool committed;
        try
        {
            committed = (saveManager?.Value ?? SaveManager.Instance).TryCommitEconomy([connection.ActiveChar], context =>
            {
                if (!house.Save(context))
                    throw new InvalidOperationException("The new house could not be saved.");
            });
        }
        catch
        {
            // A commit exception has an unknown SQL result. Keep house and payment together.
            inventory.PreservePreparedState();
            throw;
        }
        if (!committed)
        {
            _houses.Remove(house.Id);
            _housesTl.Remove(house.TlId);
            housingIdManager.ReleaseId(house.Id);
            housingTldManager.ReleaseId(house.TlId);
            objectIdManager.ReleaseId(house.ObjId);
            connection.ActiveChar.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
            return;
        }
        inventory.Complete();
        connection.ActiveChar.SendPacket(new SCMyHousePacket(house));
        house.Spawn();
        UpdateTaxInfo(house);

        if (house.CurrentStep == -1)
        {
            connection.ActiveChar.Achievements?.Increment(
                CharRecordKind.MakeHousing,
                house.TemplateId,
                0);
        }
    }

    internal static void SetInitialTaxDates(House house, DateTime utcNow)
    {
        house.PlaceDate = utcNow;
        // Placement pays one period. A second period is the unpaid-tax grace window.
        house.ProtectionEndDate = utcNow.AddDays(2d * Math.Max(1u, AppConfiguration.Instance.World.DaysForTaxPayment));
    }

    internal static bool TryStageCreationPayment(InventoryMutation inventory, Character owner, Item design,
        int taxAmount, bool payInCertificates)
    {
        if (taxAmount < 0 || !inventory.TryConsume(owner.Inventory.Bag, design, 1))
            return false;
        return TryStageTaxPayment(inventory, owner, taxAmount, payInCertificates);
    }

    internal static bool TryStageTaxPayment(InventoryMutation inventory, Character owner, int taxAmount, bool payInCertificates)
    {
        if (taxAmount < 0)
            return false;
        if (!payInCertificates)
            return inventory.TryChangeMoney(owner, -taxAmount);

        var needed = (int)(((long)taxAmount + 9999) / 10000);
        foreach (var templateId in new[] { Item.BoundTaxCertificate, Item.TaxCertificate })
        {
            foreach (var certificate in owner.Inventory.Bag.Items.Where(item => item.TemplateId == templateId).ToArray())
            {
                var count = Math.Min(needed, Math.Max(0, certificate.Count - TradeReservation.GetReservedCount(certificate)));
                if (count > 0 && !inventory.TryConsume(owner.Inventory.Bag, certificate, count))
                    return false;
                needed -= count;
                if (needed == 0)
                    return true;
            }
        }
        return needed == 0;
    }

    /// <summary>
    /// Update house permission settings
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    /// <param name="permission"></param>
    public void ChangeHousePermission(GameConnection connection, ushort tlId, HousingPermission permission)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var house = GetHouseByTlId(tlId);
            if (!IsActiveSaleHouse(house) || connection?.ActiveChar == null || house.OwnerId != connection.ActiveChar.Id)
                return; // not the owner

            house.Permission = permission;
            house.BroadcastPacket(new SCHousePermissionChangedPacket(tlId, (byte)permission), false);
        }
    }

    /// <summary>
    /// Rename house
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    /// <param name="name"></param>
    public void ChangeHouseName(GameConnection connection, ushort tlId, string name)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var house = GetHouseByTlId(tlId);
            if (!IsActiveSaleHouse(house) || connection?.ActiveChar == null || house.OwnerId != connection.ActiveChar.Id)
                return;

            house.Name = string.Concat(name.Substring(0, 1).ToUpper(), name.AsSpan(1));
            house.IsDirty = true; // Manually set the IsDirty on House level
            connection.SendPacket(new SCUnitNameChangedPacket(house.ObjId, house.Name));
        }
    }

    /// <summary>
    /// Start demolishing of a house
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="house"></param>
    /// <param name="failedToPayTax"></param>
    /// <param name="forceRestoreAllDecor"></param>
    public void Demolish(GameConnection connection, House house, bool failedToPayTax, bool forceRestoreAllDecor)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!IsActiveSaleHouse(house) || house.OwnerId == 0)
            {
                connection?.ActiveChar?.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
                return;
            }
            if (failedToPayTax && house.ProtectionEndDate > DateTime.UtcNow)
                return; // the queued tax-expiry check was superseded by a payment

            // Check if owner
            if (connection is null || house.OwnerId == connection.ActiveChar?.Id)
            {
                if (connection != null && !failedToPayTax && house.TaxDueDate <= DateTime.UtcNow)
                {
                    connection.ActiveChar.SendErrorMessage(ErrorMessageType.HouseCannotDemolishUnpaidTax);
                    return;
                }
                var ownerChar = worldManager.GetCharacterById(house.OwnerId);

                lock (house.TaxPaymentSyncRoot)
                {
                    // Mark it as expired protection and remove tax mail before changing ownership.
                    house.ProtectionEndDate = DateTime.UtcNow.AddSeconds(-1);
                    UpdateTaxInfo(house);
                    ReturnHouseItemsToOwner(house, failedToPayTax, forceRestoreAllDecor, null);

                    house.OwnerId = 0;
                    house.CoOwnerId = 0;
                    house.AccountId = 0;
                    house.SellPrice = 0;
                    house.SellToPlayerId = 0;
                    house.Permission = HousingPermission.Public;
                }
                house.BroadcastPacket(new SCHouseDemolishedPacket(house.TlId), false);

                ownerChar?.SendPacket(new SCMyHouseRemovedPacket(house.TlId));
                // Make killable
                UpdateHouseFaction(house, FactionsEnum.Monstrosity);

                SetForSaleMarkers(house, false);

                house.IsDirty = true;

                // TODO: better house killing handling
                _removedHousings.Add(house.Id);
            }
            else
            {
                // Non-owner should not be able to press demolish
                connection?.ActiveChar?.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
            }
        }
    }

    /// <summary>
    /// Fully removes a house from the world
    /// </summary>
    /// <param name="house"></param>
    public void RemoveDeadHouse(House house)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            RemoveDeadHouseLocked(house);
        }
    }

    private void RemoveDeadHouseLocked(House house)
    {
        if (!IsActiveSaleHouse(house))
            return;
        // Remove house from housing tables
        _removedHousings.Add(house.Id);
        _houses.Remove(house.Id);
        _housesTl.Remove(house.TlId);
        housingTldManager.ReleaseId(house.TlId);
        housingIdManager.ReleaseId(house.Id);
        // TODO: not sure how to handle this, just instant delete it for now
        house.Delete();
        // TODO: Add to despawn handler
        //house.Despawn = DateTime.UtcNow.AddSeconds(20);
        //SpawnManager.Instance.AddDespawn(house);
    }

    /// <summary>
    /// Helper function to calculate due tax
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="newHouseTemplate"></param>
    /// <param name="buildingNewHouse"></param>
    /// <param name="totalTaxToPay"></param>
    /// <param name="heavyHouseCount"></param>
    /// <param name="normalHouseCount"></param>
    /// <param name="hostileTaxRate"></param>
    /// <param name="oneWeekTaxCount"></param>
    /// <returns></returns>
    public bool CalculateBuildingTaxInfo(uint accountId, HousingTemplate newHouseTemplate, bool buildingNewHouse, out int totalTaxToPay, out int heavyHouseCount, out int normalHouseCount, out int hostileTaxRate, out int oneWeekTaxCount)
    {
        totalTaxToPay = 0;
        heavyHouseCount = 0;
        normalHouseCount = 0;
        hostileTaxRate = 0; // NOTE: When castles are added, this needs to be updated depending on ruling guild's settings
        oneWeekTaxCount = 0;

        var userHouses = new Dictionary<uint, House>();
        if (GetByAccountId(userHouses, accountId) <= 0 && !buildingNewHouse)
            return false;

        // Count the houses on this account
        foreach (var h in userHouses)
        {
            if (h.Value.Template.HeavyTax)
                heavyHouseCount++;
            else
                normalHouseCount++;
        }

        // If this is for a new building, add 1 to count
        if (buildingNewHouse)
        {
            if (newHouseTemplate.HeavyTax)
                heavyHouseCount++;
            else
                normalHouseCount++;
        }

        // Default Heavy Tax formula for 1.2
        var taxMultiplier = (heavyHouseCount < MaxHeavyTaxCounted ? heavyHouseCount : MaxHeavyTaxCounted) * 0.5f;
        // If less than 3 properties, or not a heavy tax property, no extra multiplier needed
        if (heavyHouseCount < 3 || newHouseTemplate.HeavyTax == false)
            taxMultiplier = 1f;

        totalTaxToPay = oneWeekTaxCount = (int)Math.Ceiling(newHouseTemplate.Taxation.Tax * taxMultiplier);

        // If this is a new house, add the deposit (base tax * 2)
        if (buildingNewHouse)
            totalTaxToPay += (int)(newHouseTemplate.Taxation.Tax * 2);

        return true;
    }

    /// <summary>
    /// This function updates related tax mails of a house (if needed)
    /// </summary>
    /// <param name="house"></param>
    public void UpdateTaxInfo(House house)
    {
        EnsureTaxMail(house, false);
    }

    /// <summary>
    /// Creates the next optional tax prepayment mail when the configured limit allows it.
    /// </summary>
    /// <param name="house"></param>
    public void OfferTaxPrepayment(House house)
    {
        EnsureTaxMail(house, true);
    }

    private void EnsureTaxMail(House house, bool allowPrepayment)
    {
        lock (house.TaxPaymentSyncRoot)
        {
            var utcNow = DateTime.UtcNow;
            var isDemolished = house.ProtectionEndDate <= utcNow;
            var isTaxDue = house.TaxDueDate <= utcNow;

            // Update Buffs (if needed)
            SetUntouchable(house, !isDemolished);
            SetRemovalDebuff(house, isDemolished);

            var allMails = mailManager.GetMyHouseMails(house.Id);

            if (house.OwnerId <= 0 || isDemolished)
            {
                if (allMails.Count > 0)
                    mailManager.DeleteHouseMails(house.Id);
                return;
            }

            var hasMailForCurrentOwner = allMails.Any(mail => mail.Header.ReceiverId == house.OwnerId);
            var mustReconcile = allMails.Count > 1 || allMails.Any(mail => mail.Header.ReceiverId != house.OwnerId);
            if (mustReconcile)
            {
                // Preserve a valid prepayment offer while removing duplicates, but never transfer an old owner's bill.
                allowPrepayment |= !isTaxDue && hasMailForCurrentOwner;
                mailManager.DeleteHouseMails(house.Id);
                allMails = [];
            }

            var shouldCreateMail = isTaxDue || (allowPrepayment && CanPayTaxMail(house));
            if (allMails.Count <= 0)
            {
                if (shouldCreateMail)
                    SendTaxMail(house);
                return;
            }

            if (isTaxDue && !MailForTax.UpdateTaxInfo(allMails[0], house))
            {
                mailManager.DeleteHouseMails(house.Id);
                SendTaxMail(house);
                return;
            }

            if (isTaxDue)
                Logger.Trace($"Tax Mail {allMails[0].Id} updated for {house.Name} ({house.Id}) owned by {house.OwnerId}");
        }
    }

    private static void SendTaxMail(House house)
    {
        var newMail = new MailForTax(house);
        if (!newMail.FinalizeMail() || !newMail.Send())
        {
            Logger.Error($"Failed to send Tax Mail for {house.Name} ({house.Id}) owned by {house.OwnerId}");
            return;
        }

        Logger.Trace($"New Tax Mail sent for {house.Name} ({house.Id}) owned by {house.OwnerId}");
    }

    public int? GetWeeklyTaxAmount(House house)
    {
        return CalculateBuildingTaxInfo(house.AccountId, house.Template, false, out _, out _, out _, out _, out var oneWeekTaxCount)
            ? HouseTaxAmount.WithLateFee(oneWeekTaxCount, house.TaxDueDate, DateTime.UtcNow,
                AppConfiguration.Instance.World.HouseLateFeePercent)
            : null;
    }

    public bool CanPayTaxMail(House house)
    {
        var utcNow = DateTime.UtcNow;
        return house.TaxDueDate <= utcNow || IsTaxPrepaymentAllowed(
            house.TaxDueDate,
            utcNow,
            AppConfiguration.Instance.World.DaysForTaxPayment,
            AppConfiguration.Instance.World.MaxTaxPrepaymentPeriods);
    }

    internal static bool IsTaxPrepaymentAllowed(DateTime taxDueDate, DateTime utcNow, uint periodDays, uint maxPrepaymentPeriods)
    {
        if (maxPrepaymentPeriods == 0)
            return false;

        var normalizedPeriodDays = Math.Max(1u, periodDays);
        var prepaymentLimit = utcNow.AddDays((double)normalizedPeriodDays * maxPrepaymentPeriods);
        return taxDueDate <= prepaymentLimit;
    }

    /// <summary>
    /// Adds a week to the protection end date (pay 1 week's tax)
    /// </summary>
    /// <param name="house"></param>
    /// <returns></returns>
    public bool PayWeeklyTax(House house)
    {
        lock (house.TaxPaymentSyncRoot)
        {
            if (house.ProtectionEndDate <= DateTime.UtcNow || !CanPayTaxMail(house))
                return false;

            house.ProtectionEndDate = house.ProtectionEndDate.AddDays(Math.Max(1u, AppConfiguration.Instance.World.DaysForTaxPayment));
            return true;
        }
    }

    /// <summary>
    /// Get house by DB Id
    /// </summary>
    /// <param name="houseId"></param>
    /// <returns></returns>
    public House GetHouseById(uint houseId)
    {
        return _houses.GetValueOrDefault(houseId);
    }

    /// <summary>
    /// Get house by TlId
    /// </summary>
    /// <param name="houseTlId"></param>
    /// <returns></returns>
    private House GetHouseByTlId(ushort houseTlId)
    {
        var house = _housesTl.GetValueOrDefault(houseTlId);
        return house != null && house.TlId == houseTlId ? house : null;
    }

    /// <summary>
    /// Changes the faction of the house
    /// </summary>
    /// <param name="house"></param>
    /// <param name="factionId"></param>
    private void UpdateHouseFaction(House house, FactionsEnum factionId)
    {
        house.BroadcastPacket(new SCUnitFactionChangedPacket(house.ObjId, house.Name, house.Faction?.Id ?? 0, factionId, false), true);
        house.Faction = factionManager.GetFaction(factionId);
    }

    /// <summary>
    /// Helper function for when the owning character changes faction
    /// </summary>
    /// <param name="characterId"></param>
    /// <param name="factionId"></param>
    public void UpdateOwnedHousingFaction(uint characterId, FactionsEnum factionId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            UpdateOwnedHousingFactionLocked(characterId, factionId);
        }
    }

    private void UpdateOwnedHousingFactionLocked(uint characterId, FactionsEnum factionId)
    {
        // TODO: Does this also need to be done when temporary changing factions? (like arena)
        var myHouses = new Dictionary<uint, House>();
        GetByCharacterId(myHouses, characterId);
        foreach (var h in myHouses)
            if (h.Value.Faction == null || h.Value.Faction.Id != factionId)
                UpdateHouseFaction(h.Value, factionId);
    }

    /// <summary>
    /// Returns furniture of a house that's being demolished or sold
    /// </summary>
    /// <param name="house"></param>
    /// <param name="failedToPayTax">Set true if demolishing due to failed tax, this adds a delay to the mail</param>
    /// <param name="forceRestoreAllDecor">For GM commands or server merges. Will try to send ALL placed furniture if set to true, even those that normally don't get returned.</param>
    /// <param name="newOwner">New owner Character if buying, otherwise leave null</param>
    private void ReturnHouseItemsToOwner(House house, bool failedToPayTax, bool forceRestoreAllDecor, Character newOwner)
    {
        if (house.OwnerId <= 0)
            return;

        var returnedItems = new List<Item>();
        var returnedMoney = 0;

        // If returning items because of a new House Owner, then don't include the design
        if (newOwner == null)
        {
            // TODO: proper grades for design
            // TODO: for future versions: Support Full-Kit demolition
            var designItemId = HousingGameData.Instance.GetItemIdByDesign(house.Template.Id);
            var designItem = itemManager.Create(designItemId, 1, 0);
            var designTemplate = itemManager.GetTemplate(designItemId);
            if (designTemplate != null && designItem != null)
            {
                designItem.Grade = designTemplate.FixedGrade >= 0 ? (byte)designTemplate.FixedGrade : (byte)0;
                designItem.OwnerId = house.OwnerId;
                designItem.SlotType = SlotType.Mail;
                returnedItems.Add(designItem);
            }
            else
            {
                Logger.Error($"Was unable to find design items for demolishing {house.Name} ({house.Id}). HouseTemplateId: {house.Template.Id}, DesignItemId: {designItemId}");
            }

            // Return taxes
            if (!failedToPayTax)
            {
                if (FeaturesManager.Fsets.Check(Models.Game.Features.Feature.taxItem))
                {
                    var taxItem = itemManager.Create(Item.BoundTaxCertificate, (int)(house.Template.Taxation.Tax / 5000), 0);
                    taxItem.OwnerId = house.OwnerId;
                    taxItem.SlotType = SlotType.Mail;
                    returnedItems.Add(taxItem);
                }
                else
                {
                    returnedMoney = (int)(house.Template.Taxation.Tax * 2);
                }
            }
        }

        var furniture = house.ParentWorld.GetDoodadByHouseDbId(house.Id);
        foreach (var f in furniture)
        {
            // Ignore attached objects (those are doors/windows etc)
            if (f.AttachPoint != AttachPointKind.None)
                continue;

            // Ignore for sale signs
            if (f.TemplateId == ForSaleMarkerDoodadId)
                continue;

            var decoDesign = HousingGameData.Instance.GetDecorationDesignFromDoodadId(f.TemplateId);
            if (decoDesign == null)
            {
                // Is not furniture, probably plants or backpacks
                f.Transform.DetachAll();
                f.ParentObjId = 0;
                f.ParentObj = null;
                f.OwnerDbId = 0;
                // TODO: probably needs to send a packet as well here
                continue;
            }

            var decoInfo = HousingGameData.Instance.GetItemHousingDecorations(decoDesign.Id);
            if (decoInfo == null)
            {
                // No design info for this item ? Just detach it for now
                f.Transform.DetachAll();
                f.ParentObjId = 0;
                f.ParentObj = null;
                f.OwnerDbId = 0;
                Logger.Warn($"ReturnHouseItemsToOwner - Furniture has a design, but couldn't find a item for it, DoodadObjId:{f.ObjId} Template:{f.TemplateId}, DesignId: {decoDesign.Id}");
                continue;
            }

            var thisDoodadsItem = itemManager.GetItemByItemId(f.ItemId);
            var returnedThisItem = false;

            var wantReturned = (newOwner == null && decoInfo.Restore) || forceRestoreAllDecor;

            // If item is bound, always return it owner
            if (f.ItemId > 0)
            {
                var item = itemManager.GetItemByItemId(f.ItemId);
                if (item.ItemFlags.HasFlag(ItemFlag.SoulBound))
                    wantReturned = true;
            }

            // If this doodad is a Coffer and has a ItemContainer attached, also return all item of that container
            if (f is DoodadCoffer coffer && f.GetItemContainerId() > 0)
            {
                // TODO: Check if items should stay in the coffer when house is sold.
                // Move it to new owner's SystemContainer first so they don't get destroyed
                var ownerSystemContainer = itemManager.GetItemContainerForCharacter(house.OwnerId, SlotType.System, null, 0);
                for (var i = coffer.ItemContainer.Items.Count - 1; i >= 0; i--)
                {
                    var cofferItem = coffer.ItemContainer.Items[i];
                    //if (cofferItem.HasFlag(ItemFlag.SoulBound) || forceRestoreAllDecor)
                    {
                        ownerSystemContainer?.AddOrMoveExistingItem(ItemTaskType.Invalid, cofferItem);
                        returnedItems.Add(cofferItem);
                    }
                }
            }

            // If the decoration item isn't marked as Restore, then just delete it (and it's possibly attached item)
            if (!wantReturned)
            {
                // Non-restore-able item
                if (newOwner == null)
                {
                    // Just delete the doodad and attached item if no new owner
                    // Delete the attached item
                    if (f.ItemId != 0)
                        thisDoodadsItem._holdingContainer?.ConsumeItem(ItemTaskType.Invalid,
                            thisDoodadsItem.TemplateId, thisDoodadsItem.Count, thisDoodadsItem);

                    // Is furniture, but doesn't restore, destroy it
                    f.Transform.DetachAll();
                    f.ItemId = 0;
                    f.Delete();
                }
                else
                {
                    // Move the doodad and item to the new owner
                    if (f.ItemId != 0)
                    {
                        // If a single item is attached, change it's owner and location
                        var item = itemManager.GetItemByItemId(f.ItemId);
                        newOwner.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.Invalid, item);
                    }
                    // Change doodad owner
                    f.OwnerId = newOwner.Id;
                }

                continue;
            }

            // Item needs to be actually returned, so let's do that
            if (f.ItemId > 0)
            {
                // Ignore if it's not in a System container for whatever reason
                if (thisDoodadsItem is { SlotType: SlotType.System })
                {
                    returnedItems.Add(thisDoodadsItem);
                    returnedThisItem = true;
                    f.ItemId = 0; // don't auto-delete
                }
            }
            else
            if (f.ItemTemplateId > 0)
            {
                // try to stack stackable items
                var oldItem = returnedItems.FirstOrDefault(x => x.TemplateId == f.ItemTemplateId && x.Count < x.Template.MaxCount);

                if (oldItem != null)
                {
                    oldItem.Count++;
                }
                else
                {
                    // It's a new one, add an item slot
                    var furnitureItem = itemManager.Create(f.ItemTemplateId, 1, 0);
                    var furnitureTemplate = itemManager.GetTemplate(f.ItemTemplateId);
                    furnitureItem.Grade = furnitureTemplate.FixedGrade >= 0 ? (byte)furnitureTemplate.FixedGrade : (byte)0;
                    furnitureItem.OwnerId = house.OwnerId;
                    furnitureItem.SlotType = SlotType.Mail;
                    returnedItems.Add(furnitureItem);
                }
                returnedThisItem = true;
            }
            else
            {
                // Not sure what happened here, just ignore it
                continue;
            }

            // Set new doodad owner if needed
            if (newOwner != null)
            {
                f.OwnerId = newOwner.Id;
            }

            if (newOwner == null || returnedThisItem)
            {
                f.Transform.DetachAll();
                f.Delete();
            }
        }

        // TODO: Grab a list of items in chests

        // TODO: Proper Mail handler
        BaseMail newMail = null;
        for (var i = 0; i < returnedItems.Count; i++)
        {
            // Split items into mails of maximum 10 attachments
            if (i % 10 == 0)
            {
                // TODO: proper mail handler
                newMail = new BaseMail
                {
                    MailType = MailType.Demolish,
                    ReceiverName = nameManager.GetCharacterName(house.OwnerId), // Doesn't seem like this needs to be set
                    Header =
                    {
                        ReceiverId = house.OwnerId,
                        SenderId = 0,
                        SenderName = ".houseDemolish",
                        Extra = house.Id
                    },
                    Title = "title",
                    Body = {
                        Text = "body", // Yes, that's indeed what it needs to be set to
                        SendDate = DateTime.UtcNow,
                        RecvDate = DateTime.UtcNow.AddHours(failedToPayTax ? HoursForFailedTaxToReturnHouse : 0)
                    }
                };
            }
            // Only attach money to first mail
            if (returnedMoney > 0 && i == 0)
                newMail.AttachMoney(returnedMoney);

            // If player is loaded in at the moment (which he/she should be anyway), directly manipulate the inventory
            // If not, only change the container
            var onlineOwner = worldManager.GetCharacterById((uint)returnedItems[i].OwnerId);
            if (onlineOwner != null)
                onlineOwner.Inventory.MailAttachments.AddOrMoveExistingItem(ItemTaskType.Invalid, returnedItems[i]);
            else
                returnedItems[i].SlotType = SlotType.Mail;

            // Attach item
            newMail.Body.Attachments.Add(returnedItems[i]);

            // Send on last or 10th item of the mail
            if (i % 10 == 9 || i == returnedItems.Count - 1)
                newMail.Send();
        }

        if (newMail != null)
        {
            Logger.Trace($"Demolition mail sent to {newMail.ReceiverName}");
        }
    }

    /* Unused
    /// <summary>
    /// Get house design by item template
    /// </summary>
    /// <param name="itemId"></param>
    /// <returns></returns>
    private uint GetDesignByItemId(uint itemId)
    {
        var design = _housingItemHousings.FirstOrDefault(h => h.Item_Id == itemId);
        return design?.Design_Id ?? 0;
    }
    */

    /// <summary>
    /// Helper function to calculate how many Appraisal Certificates are needed to sell a house at a given price
    /// </summary>
    /// <param name="house">Not used in early versions</param>
    /// <param name="salePrice"></param>
    /// <returns></returns>
    private static int CalculateSaleCertifcates(House house, uint salePrice)
    {
        // NOTE: In earlier AA, you need 1 appraisal certificate for every 100 gold of sales price
        // TODO: In later versions, this depends on the building-type/size
        var certAmount = (int)Math.Ceiling(salePrice / CopperPerCertificate);
        if (certAmount < 1)
            certAmount = 1;
        return certAmount;
    }

    /// <summary>
    /// Sets or removes For Sale Signs on the property
    /// </summary>
    /// <param name="house"></param>
    /// <param name="isForSale"></param>
    private void SetForSaleMarkers(House house, bool isForSale)
    {
        var world = house.ParentWorld;
        if (world == null)
        {
            Logger.Warn($"Trying to set a house for sale of a house that doesn't have a world attached {house.Id}");
            return;
        }
        if (isForSale)
        {
            for (var postId = 0; postId < 4; postId++)
            {
                var xMultiplier = postId % 2 == 0 ? -1 : 1f;
                var yMultiplier = postId / 2 == 0 ? -1 : 1f;
                var zRot = (135f + 90f * postId % 360).DegToRad();

                var markerPosition = house.Transform.World.Position;
                markerPosition.X += house.Template.GardenRadius * xMultiplier;
                markerPosition.Y += house.Template.GardenRadius * yMultiplier;
                var placementPolicy = house.Transform.Parent is not null || house.Transform.StickyParent is not null
                    ? DynamicDoodadPlacementPolicy.PreserveParentedHeight
                    : DynamicDoodadPlacementPolicy.GroundToNearbySurface;
                if (!DynamicDoodadPlacement.TryResolve(world.Template.GeoData, markerPosition, placementPolicy,
                        out var resolvedMarkerPosition))
                {
                    Logger.Warn($"Cannot place for-sale marker for house {house.Id} at {markerPosition}");
                    continue;
                }

                markerPosition = resolvedMarkerPosition;
                var doodad = doodadManager.Create(house.ParentWorld, 0, ForSaleMarkerDoodadId, null, true);
                if (doodad == null)
                {
                    Logger.Warn($"Cannot create for-sale marker doodad {ForSaleMarkerDoodadId} for house {house.Id}");
                    continue;
                }

                // location
                doodad.Transform.Local.SetPosition(markerPosition);
                doodad.Transform.Local.SetZRotation(zRot);
                //doodad.Transform.WorldId = world.Template.Id;
                doodad.Transform.InstanceId = world.Id;
                doodad.ItemTemplateId = 0; // designId;
                doodad.ItemId = 0;
                doodad.OwnerId = 0;
                doodad.ParentObjId = 0;
                doodad.ParentObj = null;
                doodad.UccId = 0;
                doodad.AttachPoint = AttachPointKind.None;
                doodad.OwnerType = DoodadOwnerType.Housing;
                doodad.OwnerDbId = house.Id;
                doodad.InitDoodad();

                doodad.Spawn();
            }
        }
        else
        {
            // Get all doodads related to this house
            var thisHouseSalePosts = world.GetDoodadByHouseDbId(house.Id);
            for (var c = thisHouseSalePosts.Count - 1; c >= 0; c--)
            {
                var doodad = thisHouseSalePosts[c];
                // If it's a for sale sign, remove it
                if (doodad.TemplateId == ForSaleMarkerDoodadId)
                {
                    house.AttachedDoodads.Remove(doodad);
                    doodad.Delete();
                }
            }
        }
    }

    /// <summary>
    /// Puts up a house for sale
    /// </summary>
    /// <param name="house"></param>
    /// <param name="price"></param>
    /// <param name="buyerId">Use CharacterId for selling to a specific person</param>
    /// <param name="seller">Current owner of the property (needed to manipulate inventory)</param>
    /// <returns></returns>
    public bool SetForSale(House house, uint price, uint buyerId, Character seller)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var error = IsActiveSaleHouse(house)
                ? HousingSalePlan.ValidateListing(house, seller, price, buyerId, true, DateTime.UtcNow)
                : ErrorMessageType.InvalidHouseInfo;
            if (error != ErrorMessageType.NoErrorMessage)
            {
                seller?.SendErrorMessage(error);
                return false;
            }

            var buyerName = buyerId == 0 ? "" : nameManager.GetCharacterName(buyerId);
            if (buyerId != 0 && string.IsNullOrEmpty(buyerName))
            {
                seller.SendErrorMessage(ErrorMessageType.HouseCannotSellAsDesignatedBuyerNotFound);
                return false;
            }

            var certAmount = CalculateSaleCertifcates(house, price);
            var failure = ErrorMessageType.InvalidHouseInfo;
            var settled = ExecuteSaleSettlement(house, seller, (inventory, _, _) =>
            {
                var remaining = certAmount;
                foreach (var item in seller.Inventory.Bag.Items.Where(i => i.TemplateId == Item.AppraisalCertificate).ToArray())
                {
                    var available = Math.Max(0, item.Count - TradeReservation.GetReservedCount(item));
                    var count = Math.Min(remaining, available);
                    if (count > 0 && !inventory.TryConsume(seller.Inventory.Bag, item, count))
                        return false;
                    remaining -= count;
                    if (remaining == 0)
                        break;
                }
                if (remaining > 0)
                {
                    failure = ErrorMessageType.HouseCannotSellAsNotEnoughSeal;
                    return false;
                }
                house.SellPrice = price;
                house.SellToPlayerId = buyerId;
                return true;
            }, () =>
            {
                house.BroadcastPacket(new SCHouseSetForSalePacket(house.TlId, price, buyerId, buyerName, house.Name), false);
                SetForSaleMarkers(house, true);
            });
            if (!settled)
                seller.SendErrorMessage(failure);
            return settled;
        }
    }

    public bool SetForSale(ushort houseTlId, uint price, uint buyerId, Character seller) => SetForSale(GetHouseByTlId(houseTlId), price, buyerId, seller);

    /// <summary>
    /// Cancels a sale
    /// </summary>
    /// <param name="house"></param>
    /// <param name="seller"></param>
    /// <returns></returns>
    public bool CancelForSale(House house, Character seller)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var error = IsActiveSaleHouse(house)
                ? HousingSalePlan.ValidateCancellation(house, seller)
                : ErrorMessageType.InvalidHouseInfo;
            if (error != ErrorMessageType.NoErrorMessage)
            {
                seller?.SendErrorMessage(error);
                return false;
            }
            var certAmount = CalculateSaleCertifcates(house, house.SellPrice);
            var settled = ExecuteSaleSettlement(house, seller, (inventory, mail, _) =>
            {
                if (!inventory.TryGrant(seller.Inventory.MailAttachments, Item.AppraisalCertificate, certAmount, out var refund))
                    return false;
                foreach (var attachments in refund.Chunk(MailBody.MaxMailAttachments))
                {
                    var refundMail = CreateSaleMail(house, seller.Id, ".houseSellCancel",
                        "body('" + house.Name + "', " + Item.AppraisalCertificate + ", " + certAmount + ")");
                    refundMail.Body.Attachments.AddRange(attachments);
                    if (!mail.TryAdd(refundMail))
                        return false;
                }
                house.SellPrice = 0;
                house.SellToPlayerId = 0;
                return true;
            }, () =>
            {
                house.BroadcastPacket(new SCHouseResetForSalePacket(house.TlId, house.Name), false);
                SetForSaleMarkers(house, false);
            });
            if (!settled)
                seller.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
            return settled;
        }
    }

    public bool CancelForSale(ushort houseTlId, Character seller) => CancelForSale(GetHouseByTlId(houseTlId), seller);

    private bool IsActiveSaleHouse(House house)
    {
        return house is { Id: > 0, TlId: > 0 } &&
               ReferenceEquals(GetHouseById(house.Id), house) &&
               ReferenceEquals(GetHouseByTlId(house.TlId), house);
    }

    /// <summary>
    /// Buys the house using money amount
    /// </summary>
    /// <param name="houseTlId"></param>
    /// <param name="money"></param>
    /// <param name="character"></param>
    /// <returns>Returns true if successful</returns>
    public bool BuyHouse(ushort houseTlId, uint money, Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var house = GetHouseByTlId(houseTlId);

            if (!IsActiveSaleHouse(house))
            {
                character?.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
                return false;
            }

            if (!HousingSalePlan.TryCreate(house, character, money, DateTime.UtcNow, out var plan, out var error))
            {
                character?.SendErrorMessage(error);
                return false;
            }

            var previousOwner = plan.PreviousState.OwnerId;
            var previousOwnerName = nameManager.GetCharacterName(previousOwner);
            var settled = ExecuteSaleSettlement(house, character, (inventory, mail, furniture) =>
            {
                if (!inventory.TryChangeMoney(character, -plan.Price))
                    return false;
                var boughtMail = CreateSaleMail(house, character.Id, ".houseBought",
                    "body('" + previousOwnerName + "', '" + house.Name + "', " + plan.Price + ")");
                var profitMail = CreateSaleMail(house, previousOwner, ".houseSold",
                    "body('" + character.Name + "', '" + house.Name + "', " + plan.Price + ")");
                profitMail.Title = "title('" + character.Name + "','" + house.Name + "')";
                profitMail.Body.CopperCoins = plan.Price;
                if (!mail.TryAdd(boughtMail) || !mail.TryAdd(profitMail) || !furniture.TryPrepare(character, inventory))
                    return false;
                foreach (var (owner, returned) in furniture.ReturnedItems)
                {
                    foreach (var attachments in returned.Chunk(MailBody.MaxMailAttachments))
                    {
                        var returnedMail = CreateSaleMail(house, owner, ".houseDemolish", "body");
                        returnedMail.MailType = MailType.Demolish;
                        returnedMail.Title = "title";
                        returnedMail.Header.Extra = house.Id;
                        returnedMail.Body.Attachments.AddRange(attachments);
                        if (!mail.TryAdd(returnedMail))
                            return false;
                    }
                }
                plan.PurchasedState.Apply(house);
                foreach (var oldBill in mailManager.GetMyHouseMails(house.Id).Where(MailForTax.IsTaxMail).ToArray())
                    if (!mail.TryRemove(oldBill))
                        return false;
                if (house.TaxDueDate <= DateTime.UtcNow)
                {
                    var zone = zoneManager.GetZoneByKey(house.Transform.ZoneId);
                    if (zone == null || !CalculateBuildingTaxInfo(house.AccountId, house.Template, false,
                        out var totalTax, out var heavyCount, out var normalCount, out var hostileRate, out _))
                        return false;
                    var bill = new MailForTax(house);
                    MailForTax.ApplyTaxInfo(bill, house, character.Name, zone.GroupId,
                        totalTax, heavyCount, normalCount, hostileRate, DateTime.UtcNow);
                    if (!mail.TryAdd(bill))
                        return false;
                }
                return true;
            }, () =>
            {
                house.BroadcastPacket(new SCUnitFactionChangedPacket(house.ObjId, house.Name,
                    plan.PreviousState.Faction?.Id ?? 0, house.Faction.Id, false), true);
                house.BroadcastPacket(new SCHouseSoldPacket(house.TlId, previousOwner, character.Id,
                    character.AccountId, character.Name, house.Name), false);
                SetForSaleMarkers(house, false);
                character.SendPacket(new SCMyHousePacket(house));
                var oldOwner = worldManager.GetCharacterById(previousOwner);
                if (oldOwner is { IsOnline: true })
                    oldOwner.SendPacket(new SCMyHouseRemovedPacket(house.TlId));
            }, true);
            if (!settled)
                character.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
            return settled;
        }
    }

    private BaseMail CreateSaleMail(House house, uint receiver, string sender, string body)
    {
        return new BaseMail
        {
            MailType = MailType.HousingSale,
            Header = { ReceiverId = receiver, SenderName = sender },
            ReceiverName = nameManager.GetCharacterName(receiver),
            Title = "title(" + zoneManager.GetZoneByKey(house.Transform.ZoneId)?.GroupId + ",'" + house.Name + "')",
            Body = { Text = body, SendDate = DateTime.UtcNow, RecvDate = DateTime.UtcNow.AddMilliseconds(1) }
        };
    }

    private bool ExecuteSaleSettlement(House house, Character actor,
        Func<InventoryMutation, MailMutation, HousingFurnitureSettlement, bool> prepare,
        Action publish, bool includeFurniture = false)
    {
        var before = HousingSaleState.Capture(house);
        using var inventory = new InventoryMutation(ItemTaskType.BuyHouse);
        using var mail = mailManager.BeginMutation();
        using var furniture = includeFurniture ? new HousingFurnitureSettlement(house, itemManager) : null;
        var preserve = false;
        try
        {
            if (!prepare(inventory, mail, furniture))
                return false;
            house.IsDirty = true;
            try
            {
                if (!(saveManager?.Value ?? SaveManager.Instance).TryCommitEconomy([actor], context =>
                {
                    if (!house.Save(context))
                        throw new InvalidOperationException("The staged house could not be persisted.");
                    furniture?.Save(context);
                }))
                    return false;
            }
            catch
            {
                // The commit outcome can be uncertain. Never restore or announce this state.
                preserve = true;
                throw;
            }
            preserve = true;
            inventory.Complete();
            mail.Complete();
            furniture?.Complete();
            publish();
            return true;
        }
        finally
        {
            if (preserve)
            {
                inventory.PreservePreparedState();
                mail.PreservePreparedState();
                furniture?.PreservePreparedState();
            }
            else
            {
                inventory.Dispose();
                mail.Dispose();
                furniture?.Dispose();
                before.Apply(house);
            }
        }
    }

    /// <summary>
    /// Ticker function for checking all houses if they need tax mails sent
    /// </summary>
    public void CheckHousingTaxes()
    {
        if (_isCheckingTaxTiming)
            return;
        _isCheckingTaxTiming = true;
        try
        {
            // Logger.Trace("CheckHousingTaxes");
            var expiredHouseList = new List<House>();
            foreach (var house in _houses)
            {
                if (house.Value?.ProtectionEndDate <= DateTime.UtcNow && house.Value?.OwnerId > 0)
                    expiredHouseList.Add(house.Value);
                UpdateTaxInfo(house.Value);
            }
            foreach (var house in expiredHouseList)
            {
                Demolish(null, house, true, false);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e);
        }

        _isCheckingTaxTiming = false;
    }

    /// <summary>
    /// Places a piece of furniture at a given location, using item and design
    /// </summary>
    /// <param name="player"></param>
    /// <param name="houseTlId"></param>
    /// <param name="designId"></param>
    /// <param name="pos"></param>
    /// <param name="quat"></param>
    /// <param name="parentObjId"></param>
    /// <param name="itemId"></param>
    /// <returns></returns>
    /// <summary>
    /// Toggles the allow furniture recovery flag
    /// </summary>
    /// <param name="character"></param>
    /// <param name="houseTl"></param>
    public void HousingToggleAllowRecover(Character character, ushort houseTl)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            HousingToggleAllowRecoverLocked(character, houseTl);
        }
    }

    private void HousingToggleAllowRecoverLocked(Character character, ushort houseTl)
    {
        var house = GetHouseByTlId(houseTl);
        if (!IsActiveSaleHouse(house) || character == null)
            return;
        if (character.Id != house.OwnerId)
            return;
        house.AllowRecover = !house.AllowRecover;
        house.BroadcastPacket(new SCHousingRecoverTogglePacket(house.TlId, house.AllowRecover), false);
    }

    /// <summary>
    /// Returns a house where the given position falls within boundaries of the house 
    /// </summary>
    /// <param name="world"></param>
    /// <param name="position"></param>
    /// <returns>Target House or Null</returns>
    public House GetHouseAtLocation(WorldInstance world, Vector3 position)
    {
        if (world == null || !HousingAreaPolygon.IsFinite(position))
            return null;
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var house in _houses.Values)
            {
                if (!ReferenceEquals(house.ParentWorld, world) || house.Template == null)
                    continue;
                var origin = house.Transform.World.Position;
                if (HousingFootprint.TryCreateGarden(new Vector2(origin.X, origin.Y),
                        house.Template.GardenRadius, house.Template.Alley, out var footprint) &&
                    footprint.Contains(position.X, position.Y))
                    return house;
            }
        }
        return null;
    }

    public uint GetActAbilityBonusFromHouse(int actabilityGroupId, House house)
    {
        var res = 0u;
        if (actabilityGroupId <= 0)
            return res;

        var furniture = house.ParentWorld.GetDoodadByHouseDbId(house.Id);
        var bonusByDoodadTemplate = new Dictionary<uint, uint>(); // Make sure every furniture type only counts once
        // TODO: Implement special decor effect limit
        // This should not break gameplay as the server-side value would always be greater than or equal to what the client thinks

        foreach (var f in furniture)
        {
            // Ignore attached objects (those are doors/windows etc)
            if (f.AttachPoint != AttachPointKind.None)
                continue;

            // Ignore for sale signs
            if (f.TemplateId == ForSaleMarkerDoodadId)
                continue;
            var decoDesign = HousingGameData.Instance.GetDecorationDesignFromDoodadId(f.TemplateId);
            if (decoDesign != null && decoDesign.ActabilityGroupId == actabilityGroupId)
            {
                if (!bonusByDoodadTemplate.ContainsKey(f.TemplateId))
                    bonusByDoodadTemplate.Add(f.TemplateId, decoDesign.ActabilityUp);
            }
        }

        foreach (var bonus in bonusByDoodadTemplate.Values)
        {
            res += bonus;
        }
        return res;
    }
}
