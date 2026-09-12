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

public class ShipyardManager(ITaskManager taskManager, IObjectIdManager objectIdManager, IShipyardIdManager shipyardIdManager, IWorldManager worldManager, ITaxationsManager taxationsManager, ISkillManager skillManager) : Singleton<ShipyardManager>, IShipyardManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public Dictionary<uint, ShipyardsTemplate> _shipyardsTemplate = [];
    private Dictionary<uint, Shipyard> _shipyard = [];
    private List<uint> _removedShipyards = [];

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

    public Shipyard Create(Character owner, ShipyardData shipyardData)
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
            Transform = { InstanceId = owner.ParentWorld.Id }, TemplateId = shipyardData.TemplateId, // duplicate Id
            Id = shipyardData.TemplateId,
            Template = template,
            Faction = owner.Faction,
            Level = 30
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
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (character == null || character.Id != shipyard.ShipyardData.Type2 ||
                !taxationsManager.Taxations.TryGetValue((uint)shipyard.Template.TaxationId, out var taxation) ||
                taxation.Tax > int.MaxValue)
                return false;
            var designId = shipyard.Template.OriginItemId;
            var design = character.Inventory.Bag.Items.FirstOrDefault(item => item.TemplateId == designId);
            if (design == null)
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
        var shipId = (uint)shipyard.ShipyardData.Id;
        // Remove Shipyard from Shipyard tables
        _removedShipyards.Add(shipId);
        _shipyard.Remove(shipId);
        shipyardIdManager.ReleaseId(shipId);
        objectIdManager.ReleaseId(shipyard.ObjId);
        shipyard.Delete();
    }

    public void ShipyardCompleted(Shipyard shipyard)
    {
        var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
        var found = character.Inventory.Bag.GetAllItemsByTemplate(shipyard.Template.ItemId, -1, out var foundItems, out _);
        if (found)
        {
            // calculate skillData
            var skillData = (SkillItem)SkillCaster.GetByType(SkillCasterType.Item);
            skillData.ItemId = foundItems[0].Id;
            shipyard.ParentWorld.SlaveManager.Create(character, skillData, true, shipyard.Transform);
        }
        RemoveShipyard(shipyard);
    }

    public void ShipyardCompletedTask(Shipyard shipyard)
    {
        var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
        character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Shipyard, shipyard.Template.ItemId, 1, 0);
        var shipyardCompleteTask = new ShipyardCompleteTask { _shipyard = shipyard };

        shipyard.ShipyardData.Step = 1000; // last step, the ceremony of launching the ship
        character.BroadcastPacket(new SCShipyardStatePacket(shipyard.ShipyardData), true);

        var animTime = shipyard.Template.CeremonyAnimTime;
        taskManager.Schedule(shipyardCompleteTask, TimeSpan.FromMilliseconds(animTime));
    }

    public void ShipyardTick()
    {
        foreach (var shipyard in _shipyard)
        {
            UpdateShipyardInfo(shipyard.Value);
        }
    }

    private void UpdateShipyardInfo(Shipyard shipyard)
    {
        var isDecaying = DateTime.UtcNow >= shipyard.ShipyardData.Spawned.AddDays(3);

        SetProtectionBuff(shipyard, isDecaying);
        SetDecayBuff(shipyard, isDecaying);
    }

    private void SetProtectionBuff(Shipyard shipyard, bool isDecay)
    {
        if (!isDecay)
        {
            var duration = shipyard.ShipyardData.Spawned - DateTime.UtcNow;
            var mins = Math.Round(duration.TotalMinutes) * 60000;

            var timeleft = shipyard.Template.TaxDuration + mins;

            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.TaxProtection))
                return;

            var protectionBuffTemplate = skillManager.GetBuffTemplate((uint)BuffConstants.TaxProtection);
            if (protectionBuffTemplate != null)
            {
                var casterObj = new SkillCasterUnit(shipyard.ObjId);
                shipyard.Buffs.AddBuff(new Buff(shipyard, shipyard, casterObj, protectionBuffTemplate, null, DateTime.UtcNow), 0, (int)timeleft);
            }
            else
            {
                Logger.Error("Unable to find Protection Buff template");
            }
        }
        else
        {
            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.TaxProtection))
                shipyard.Buffs.RemoveBuff((uint)BuffConstants.TaxProtection);
        }
    }

    private void SetDecayBuff(Shipyard shipyard, bool isDecay)
    {
        if (isDecay)
        {
            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.Deterioration))
            {
                shipyard.ReduceCurrentHp(shipyard, 7);
                var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
                character.SendPacket(new SCUnitStatePacket(shipyard));
                character.SendPacket(new SCShipyardStatePacket(shipyard.ShipyardData));

                return;
            }

            var protectionBuffTemplate = skillManager.GetBuffTemplate((uint)BuffConstants.Deterioration);
            if (protectionBuffTemplate != null)
            {
                var casterObj = new SkillCasterUnit(shipyard.ObjId);
                shipyard.Buffs.AddBuff(new Buff(shipyard, shipyard, casterObj, protectionBuffTemplate, null, DateTime.UtcNow));
            }
            else
            {
                Logger.Error("Unable to find Deterioration Debuff template");
            }
        }
        else
        {
            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.Deterioration))
                shipyard.Buffs.RemoveBuff((uint)BuffConstants.Deterioration);
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
        }
    }
}
