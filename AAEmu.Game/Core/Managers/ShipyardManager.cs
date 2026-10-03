using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Tasks.Shipyard;
using AAEmu.Game.Utils.DB;

using NLog;

namespace AAEmu.Game.Core.Managers;

public partial class ShipyardManager(ITaskManager taskManager, IObjectIdManager objectIdManager, IShipyardIdManager shipyardIdManager, IWorldManager worldManager, ITaxationsManager taxationsManager, ISkillManager skillManager) : Singleton<ShipyardManager>, IShipyardManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public Dictionary<uint, ShipyardsTemplate> _shipyardsTemplate = [];
    private Dictionary<uint, Shipyard> _shipyard = [];
    internal Func<IReadOnlyCollection<Character>, Action<PersistenceSaveContext>, bool> CommitPersistence { get; set; } =
        (participants, write) => SaveManager.Instance.TryCommitEconomy(participants, write);

    public void Initialize()
    {
        Logger.Info("Initialising Shipyard Manager...");
        ShipyardTickStart();
    }

    private void ShipyardTickStart()
    {
        Logger.Warn("ShipyardUpdateInfoTick: Started");

        var shipyardTickStartTask = new ShipyardTickTask();
        taskManager.Schedule(shipyardTickStartTask, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private Shipyard Create(Character owner, ShipyardData shipyardData)
    {
        if (!_shipyardsTemplate.TryGetValue(shipyardData.TemplateId, out var template) ||
            !template.ShipyardSteps.ContainsKey(shipyardData.Step))
            return null;

        var design = ItemManager.Instance.GetTemplate(template.OriginItemId);
        if (design != null && ZoneSkillRestrictions.GetBan(owner, design.UseSkillId, design.Id,
                new System.Numerics.Vector3(shipyardData.X, shipyardData.Y, shipyardData.Z)) != null)
        {
            owner.SendErrorMessage(ErrorMessageType.ItemCannotUseHere);
            return null;
        }

        var pos = owner.Transform.CloneAsSpawnPosition();
        pos.X = shipyardData.X;
        pos.Y = shipyardData.Y;
        pos.Z = shipyardData.Z;
        pos.Yaw = shipyardData.zRot;

        var shipyard = new Shipyard
        {
            ParentWorld = owner.ParentWorld, TemplateId = shipyardData.TemplateId,
            Template = template,
            Faction = owner.Faction,
            Level = 30,
            SourceDesignItemId = shipyardData.Id
        };
        shipyard.Hp = shipyard.MaxHp;
        shipyard.Name = owner.Name;
        shipyard.ModelId = template.ShipyardSteps[shipyardData.Step].ModelId;
        shipyard.Transform.ApplyWorldSpawnPosition(pos);

        shipyard.ShipyardData = new ShipyardData { TemplateId = template.Id, X = pos.X, Y = pos.Y,
            Z = pos.Z,
            zRot = pos.Yaw,
            MoneyAmount = 0,
            Actions = shipyardData.Step,
            Type = template.OriginItemId,
            OwnerName = owner.Name,
            Type2 = owner.Id,
            Type3 = owner.Faction.Id,
            Spawned = DateTime.UtcNow,
            Hp = template.ShipyardSteps[shipyardData.Step].MaxHp * 100,
            Step = shipyardData.Step
        };

        if (!TryInstallPaidShipyard(owner, shipyard))
        {
            owner.SendErrorMessage(ErrorMessageType.NotEnoughItem);
            return null;
        }

        shipyard.Spawn();

        return shipyard;
    }

    internal bool TryInstallPaidShipyard(Character character, Shipyard shipyard)
    {
        if (character == null || shipyard == null || shipyard.Retiring || shipyard.Retired)
            return false;
        lock (SaveManager.PersistenceSyncRoot)
        lock (AccountManager.Instance.GetAccountSyncRoot(character.AccountId))
        {
            if (SkillLaborBatch.Current != null || IsCurrent(shipyard) || character.Id != shipyard.ShipyardData.Type2 ||
                !taxationsManager.Taxations.TryGetValue((uint)shipyard.Template.TaxationId, out var taxation) ||
                taxation.Tax > int.MaxValue)
                return false;
            var designId = shipyard.Template.OriginItemId;
            var design = character.Inventory.Bag.GetItemByItemId(shipyard.SourceDesignItemId);
            if (design == null || design.TemplateId != designId || design.OwnerId != character.Id ||
                design.Count < 1)
                return false;
            var reagents = skillManager.GetSkillReagentsBySkillId(design.Template.UseSkillId);
            var products = skillManager.GetSkillProductsBySkillId(design.Template.UseSkillId);
            if (reagents == null || products == null)
                return false;
            using var mutation = new InventoryMutation(ItemTaskType.Shipyard);
            if (!mutation.TryChangeMoney(character, -(int)taxation.Tax) ||
                !mutation.TryConsume(character.Inventory.Bag, design, 1))
                return false;
            foreach (var reagent in reagents)
                if (!InventoryPayment.TryConsume(mutation, character.Inventory.Bag, reagent.ItemId, reagent.Amount))
                    return false;
            foreach (var product in products)
                if (!mutation.TryGrant(character.Inventory.Bag, product.ItemId, product.Amount))
                    return false;
            // Failed payment must not allocate world or shipyard IDs. Install the
            // accepted construction before any item or quest observer can run.
            var objId = objectIdManager.GetNextId();
            uint shipId = 0;
            try
            {
                shipId = shipyardIdManager.GetNextId();
                shipyard.Id = shipId;
                shipyard.ObjId = objId;
                shipyard.ShipyardData.ObjId = objId;
                shipyard.ShipyardData.Id = shipId;
                _shipyard.Add(shipId, shipyard);
            }
            catch
            {
                objectIdManager.ReleaseId(objId);
                if (shipId != 0)
                    shipyardIdManager.ReleaseId(shipId);
                throw;
            }
            bool committed;
            try
            {
                committed = CommitPersistence([character], shipyard.Save);
            }
            catch
            {
                // A commit exception has an unknown outcome. Keep the prepared state.
                mutation.PreservePreparedState();
                throw;
            }
            if (!committed)
            {
                _shipyard.Remove(shipId);
                shipyardIdManager.ReleaseId(shipId);
                objectIdManager.ReleaseId(objId);
                return false;
            }
            try
            {
                mutation.Complete();
            }
            catch (Exception exception)
            {
                // Complete accepts the assets before notification. Continue to
                // spawn the installed construction even when an observer fails.
                Logger.Error(exception, "Shipyard payment notification failed for shipyard {0}", shipId);
            }
            return true;
        }
    }

    public void RemoveShipyard(Shipyard shipyard)
    {
        RetireShipyard(shipyard, false, null);
    }

    internal void DestroyShipyard(Shipyard shipyard, Action publishDeath, Action restoreDeath = null)
    {
        RetireShipyard(shipyard, true, publishDeath, restoreDeath);
    }

    public void ShipyardCompleted(Shipyard shipyard)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!IsCurrent(shipyard) || shipyard.ShipyardData.Step != 1000)
                return;
            if (shipyard.CeremonyEnd > DateTime.UtcNow)
            {
                ScheduleCompletion(shipyard);
                return;
            }
            var character = worldManager.GetCharacterById(shipyard.ShipyardData.Type2);
            var item = character?.Inventory.Bag.GetItemByItemId(shipyard.CompletionItemId);
            if (!RetireShipyard(shipyard, false, null))
                return;
            if (item != null && item.TemplateId == shipyard.Template.ItemId &&
                ReferenceEquals(character.ParentWorld, shipyard.ParentWorld))
            {
                var skillData = new SkillItem { ItemId = item.Id };
                shipyard.ParentWorld.SlaveManager.Create(character, skillData, true, shipyard.Transform);
            }
        }
    }

    public void ShipyardCompletedTask(Shipyard shipyard)
    {
        var character = worldManager.GetCharacterById(shipyard.ShipyardData.Type2);
        if (character == null)
            return;
        var skill = new Skill(new Models.Game.Skills.Templates.SkillTemplate())
        {
            CommitLaborBatch = (owner, write) => CommitPersistence([owner], write)
        };
        SkillLaborBatch.Run(character, skill, false,
            () => Models.Game.Skills.Effects.CraftEffect.CompleteShipyardConstruction(character, shipyard, skill),
            ItemTaskType.Shipyard);
    }

    internal void ScheduleCompletion(Shipyard shipyard)
    {
        var delay = shipyard.CeremonyEnd - DateTime.UtcNow;
        taskManager.Schedule(new ShipyardCompleteTask { _shipyard = shipyard },
            delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
    }

    public void ShipyardTick()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var shipyard in _shipyard.Values.ToArray())
            {
                if (shipyard.Retiring)
                    continue;
                if (shipyard.ShipyardData.Step == 1000)
                {
                    if (shipyard.CeremonyEnd <= DateTime.UtcNow)
                        ShipyardCompleted(shipyard);
                    continue;
                }
                if (shipyard.Hp <= 0)
                {
                    DestroyShipyard(shipyard, null);
                    continue;
                }
                UpdateShipyardInfo(shipyard, true);
            }
        }
    }

    private void UpdateShipyardInfo(Shipyard shipyard, bool applyDecay)
    {
        var timeLeft = shipyard.ShipyardData.Spawned.AddMilliseconds(shipyard.Template.TaxDuration) - DateTime.UtcNow;
        var isDecaying = timeLeft <= TimeSpan.Zero;
        var desired = isDecaying ? BuffConstants.Deterioration : BuffConstants.TaxProtection;
        var removed = isDecaying ? BuffConstants.TaxProtection : BuffConstants.Deterioration;
        if (shipyard.Buffs.CheckBuff((uint)removed))
            shipyard.Buffs.RemoveBuff((uint)removed);
        if (!shipyard.Buffs.CheckBuff((uint)desired))
        {
            var template = skillManager.GetBuffTemplate((uint)desired);
            if (template != null)
                shipyard.Buffs.AddBuff(new Buff(shipyard, shipyard, new SkillCasterUnit(shipyard.ObjId), template,
                    null, DateTime.UtcNow), 0, isDecaying ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(timeLeft.TotalMilliseconds)));
        }
        if (isDecaying && applyDecay)
        {
            shipyard.ReduceCurrentHp(shipyard, 7);
            if (IsCurrent(shipyard))
            {
                shipyard.BroadcastPacket(new SCUnitStatePacket(shipyard), true);
                shipyard.BroadcastPacket(new SCShipyardStatePacket(shipyard.ShipyardData), true);
            }
        }
    }

    public void Load()
    {
        Logger.Info("Loading Shipyards...");
        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM shipyards";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ShipyardsTemplate
                        {
                            Id = reader.GetUInt32("id"),
                            Name = reader.GetString("name"),
                            MainModelId = reader.GetUInt32("main_model_id"),
                            ItemId = reader.GetUInt32("item_id"),
                            CeremonyAnimTime = reader.GetInt32("ceremony_anim_time"),
                            SpawnOffsetFront = reader.GetFloat("spawn_offset_front"),
                            SpawnOffsetZ = reader.GetFloat("spawn_offset_z"),
                            BuildRadius = reader.GetInt32("build_radius"),
                            TaxDuration = reader.GetInt32("tax_duration", 0),
                            OriginItemId = reader.GetUInt32("origin_item_id", 0),
                            TaxationId = reader.GetInt32("taxation_id")
                        };
                        _shipyardsTemplate.Add(template.Id, template);
                    }
                }
            }
            Logger.Info("Loaded {0} shipyards", _shipyardsTemplate.Count);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM shipyard_steps";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ShipyardSteps
                        {
                            Id = reader.GetUInt32("id"),
                            ShipyardId = reader.GetUInt32("shipyard_id"),
                            Step = reader.GetInt32("step"),
                            ModelId = reader.GetUInt32("model_id"),
                            SkillId = reader.GetUInt32("skill_id"),
                            NumActions = reader.GetInt32("num_actions"),
                            MaxHp = reader.GetInt32("max_hp")
                        };
                        if (_shipyardsTemplate.TryGetValue(template.ShipyardId, out var value))
                        {
                            value.ShipyardSteps.Add(template.Step, template);
                        }
                    }
                }
            }
            LoadSalvageRewards(connection);
            _designShipyards = ReadDesignShipyards(connection);
        }
    }
}
