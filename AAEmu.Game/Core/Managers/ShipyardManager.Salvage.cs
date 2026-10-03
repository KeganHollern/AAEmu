using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.Core.Managers;

public partial class ShipyardManager
{
    private Dictionary<uint, List<ShipyardReward>> _shipyardRewards = [];

    private void LoadSalvageRewards(SqliteConnection connection)
    {
        _shipyardRewards = ReadSalvageRewards(connection);
    }

    internal static Dictionary<uint, List<ShipyardReward>> ReadSalvageRewards(SqliteConnection connection)
    {
        var rewards = new Dictionary<uint, List<ShipyardReward>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, shipyard_id, doodad_id, on_water, radius, count FROM shipyard_rewards ORDER BY id";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var shipyardId = reader.GetUInt32("shipyard_id");
            var reward = new ShipyardReward
            {
                Id = reader.GetUInt32("id"),
                DoodadId = reader.GetUInt32("doodad_id"),
                OnWater = reader.GetBoolean("on_water", true),
                Radius = reader.GetFloat("radius"),
                Count = reader.GetInt32("count")
            };
            if (shipyardId == 0 || reward.DoodadId == 0 || reward.Count <= 0 ||
                !float.IsFinite(reward.Radius) || reward.Radius < 0)
                throw new InvalidDataException($"Shipyard reward {reward.Id} is invalid.");
            if (!rewards.TryGetValue(shipyardId, out var entries))
                rewards.Add(shipyardId, entries = []);
            entries.Add(reward);
        }
        return rewards;
    }

    private ShipyardSalvage PrepareSalvage(Shipyard shipyard)
    {
        var doodads = new List<Doodad>();
        var salvage = new ShipyardSalvage(doodads, doodad =>
        {
            if (doodad.DbId != 0)
                DoodadIdManager.Instance.ReleaseId(doodad.DbId);
            objectIdManager.ReleaseId(doodad.ObjId);
        });
        try
        {
            if (!_shipyardRewards.TryGetValue(shipyard.TemplateId, out var rewards))
                return salvage;

            var world = shipyard.ParentWorld;
            // Existing persistent world doodads reload in main_world only.
            if (world?.Template.Id != WorldManager.DefaultWorldTemplateId)
                throw new InvalidOperationException("Shipyard salvage needs the persistent main world.");

            foreach (var reward in rewards)
            {
                for (var index = 0; index < reward.Count; index++)
                {
                    var doodad = DoodadManager.Instance.Create(world, 0, reward.DoodadId, null, true)
                        ?? throw new InvalidOperationException($"Shipyard reward doodad {reward.DoodadId} is unavailable.");
                    doodads.Add(doodad);
                    doodad.DbId = DoodadIdManager.Instance.GetNextId();
                    doodad.Transform.Local.SetPosition(reward.GetPosition(shipyard.Transform.World.Position,
                        Random.Shared, (position, onWater) => onWater
                            ? world.Water.GetWaterSurface(position, out _)
                            : world.Template.GeoData.GetHeight(position)));
                    doodad.Transform.Local.SetRotation(0, 0, (float)(Random.Shared.NextDouble() * Math.Tau));
                    doodad.InitializeGrowthTime();
                    if (doodad.PhaseTime == DateTime.MinValue)
                        doodad.PhaseTime = doodad.PlantTime;
                    doodad.OverridePhaseTime = doodad.PhaseTime;
                    doodad.IsPersistent = true;
                }
            }
            return salvage;
        }
        catch
        {
            salvage.Cancel();
            throw;
        }
    }

    internal sealed class ShipyardSalvage(List<Doodad> doodads, Action<Doodad> releaseIds)
    {
        private bool _finished;

        public void Save(PersistenceSaveContext context)
        {
            if (_finished)
                throw new InvalidOperationException("Shipyard salvage already finished.");
            foreach (var doodad in doodads)
                doodad.SaveLaborState(context, false);
        }

        public void Publish()
        {
            if (_finished)
                return;
            _finished = true;
            // The transaction already owns these rows. A publish failure must
            // leave their IDs reserved so startup can restore the saved debris.
            foreach (var doodad in doodads)
            {
                try
                {
                    doodad.ParentWorld.SpawnManager.AddPlayerDoodad(doodad);
                    doodad.InitDoodad();
                    doodad.Spawn();
                }
                catch (Exception exception)
                {
                    Logger.Error(exception, "Cannot publish shipyard salvage doodad {0}", doodad.DbId);
                    SaveManager.Instance.FailForConsistency(exception);
                    throw;
                }
            }
        }

        public void Cancel()
        {
            if (_finished)
                return;
            _finished = true;
            foreach (var doodad in doodads)
            {
                doodad.IsPersistent = false;
                releaseIds(doodad);
            }
        }
    }
}
