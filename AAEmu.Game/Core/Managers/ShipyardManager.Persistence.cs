using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public partial class ShipyardManager
{
    internal bool IsCurrent(Shipyard shipyard) => shipyard != null && !shipyard.Retired &&
        _shipyard.TryGetValue((uint)shipyard.ShipyardData.Id, out var current) && ReferenceEquals(current, shipyard);

    public void Save(PersistenceSaveContext context)
    {
        foreach (var shipyard in _shipyard.Values)
            shipyard.Save(context);
    }

    internal IReadOnlyList<Shipyard> LoadPlayerShipyards(WorldInstance world, bool publish = true)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var loaded = new List<Shipyard>();
            using (var connection = MySQL.CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM shipyards WHERE world_id=@world AND instance_id=@instance ORDER BY id";
                command.Parameters.AddWithValue("@world", world.Template.Id);
                command.Parameters.AddWithValue("@instance", world.Id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetUInt32("id");
                    if (_shipyard.ContainsKey(id))
                        continue;
                    var templateId = reader.GetUInt32("template_id");
                    if (!_shipyardsTemplate.TryGetValue(templateId, out var template))
                        throw new InvalidOperationException($"Saved shipyard {id} uses unknown template {templateId}.");
                    var data = new ShipyardData
                    {
                        Id = id, TemplateId = templateId,
                        Type2 = reader.GetUInt32("owner_id"), OwnerName = reader.GetString("owner_name"),
                        Type3 = (FactionsEnum)reader.GetUInt32("faction_id"), Type = template.OriginItemId,
                        X = reader.GetFloat("x"), Y = reader.GetFloat("y"), Z = reader.GetFloat("z"),
                        zRot = reader.GetFloat("yaw"), Spawned = DateTime.SpecifyKind(reader.GetDateTime("spawned"), DateTimeKind.Utc),
                        Hp = reader.GetInt32("state_hp")
                    };
                    var shipyard = new Shipyard
                    {
                        Id = id, TemplateId = templateId, Template = template, ShipyardData = data,
                        ParentWorld = world, Level = 30, Name = data.OwnerName,
                        Faction = FactionManager.Instance.GetFaction(data.Type3),
                        Hp = reader.GetInt32("hp"), Mp = reader.GetInt32("mp"),
                        CompletionItemId = reader.GetUInt64("completion_item_id"),
                        CeremonyEnd = reader.IsDBNull(reader.GetOrdinal("ceremony_end")) ? DateTime.MinValue :
                            DateTime.SpecifyKind(reader.GetDateTime("ceremony_end"), DateTimeKind.Utc)
                    };
                    shipyard.Transform.Local.SetPosition(data.X, data.Y, data.Z);
                    shipyard.Transform.Local.SetRotation(0, 0, data.zRot);
                    shipyard.RestoreConstruction(reader.GetInt32("current_step"), reader.GetInt32("current_action"),
                        shipyard.CeremonyEnd != DateTime.MinValue);
                    if ((shipyard.CeremonyEnd != DateTime.MinValue) != (shipyard.CompletionItemId != 0))
                        throw new InvalidOperationException($"Saved shipyard {id} has an incomplete reward marker.");
                    data.ObjId = shipyard.ObjId = objectIdManager.GetNextId();
                    shipyard.IsDirty = false;
                    _shipyard.Add(id, shipyard);
                    loaded.Add(shipyard);
                }
            }
            if (publish)
                foreach (var shipyard in loaded)
                {
                    if (shipyard.Hp <= 0 && shipyard.ShipyardData.Step != 1000)
                    {
                        if (!RetireShipyard(shipyard, true, null))
                            throw new InvalidOperationException("A destroyed saved shipyard could not be retired.");
                        continue;
                    }
                    shipyard.Spawn();
                    if (shipyard.ShipyardData.Step == 1000)
                        ScheduleCompletion(shipyard);
                    else
                        UpdateShipyardInfo(shipyard, false);
                }
            Logger.Info("Loaded {0} player shipyards for world {1}", loaded.Count, world.Id);
            return loaded;
        }
    }

    private bool RetireShipyard(Shipyard shipyard, bool destroyed, Action publishDeath, Action restoreDeath = null)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!IsCurrent(shipyard) || shipyard.Retiring)
                return false;
            ShipyardSalvage salvage;
            try
            {
                salvage = destroyed ? PrepareSalvage(shipyard) : null;
            }
            catch
            {
                restoreDeath?.Invoke();
                throw;
            }
            shipyard.Retiring = true;
            void Write(PersistenceSaveContext context)
            {
                salvage?.Save(context);
                using var command = context.Connection.CreateCommand();
                command.Transaction = context.Transaction;
                command.CommandText = "DELETE FROM shipyards WHERE id=@id";
                command.Parameters.AddWithValue("@id", shipyard.ShipyardData.Id);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The retiring shipyard row is missing.");
            }
            void Restore()
            {
                shipyard.Retiring = false;
                salvage?.Cancel();
                restoreDeath?.Invoke();
            }
            void Publish()
            {
                try
                {
                    publishDeath?.Invoke();
                    salvage?.Publish();
                    shipyard.Retired = true;
                    _shipyard.Remove((uint)shipyard.ShipyardData.Id);
                    shipyard.Delete();
                    shipyardIdManager.ReleaseId((uint)shipyard.ShipyardData.Id);
                    objectIdManager.ReleaseId(shipyard.ObjId);
                }
                catch (Exception exception)
                {
                    SaveManager.Instance.FailForConsistency(exception);
                    throw;
                }
            }
            var batch = SkillLaborBatch.Current;
            if (batch != null)
            {
                batch.Enlist(Write, Restore);
                batch.AfterCommit(Publish);
                return true;
            }
            if (!CommitPersistence([], Write))
            {
                Restore();
                return false;
            }
            Publish();
            return true;
        }
    }
}
