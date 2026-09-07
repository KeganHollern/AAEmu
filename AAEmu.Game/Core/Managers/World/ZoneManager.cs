using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Zones;
using AAEmu.Game.Utils.DB;

using MySql.Data.MySqlClient;
using NLog;

namespace AAEmu.Game.Core.Managers.World;

public class ZoneManager(IWorldManager worldManager, ITaskManager taskManager) : Singleton<ZoneManager>, IZoneManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, uint> _zoneIdToKey;
    private Dictionary<uint, Zone> _zones;
    private Dictionary<uint, ZoneGroup> _groups;
    private Dictionary<ushort, ZoneConflict> _conflicts;
    private Dictionary<uint, ZoneGroupBannedTag> _groupBannedTags;
    private Dictionary<uint, ZoneClimateElem> _climateElem;
    private bool _initialized;

    public ZoneConflict[] GetConflicts() => _conflicts.Values.ToArray();

    public Zone GetZoneById(uint zoneId)
    {
        return _zoneIdToKey.TryGetValue(zoneId, out var value) ? _zones[value] : null;
    }

    public Zone GetZoneByKey(uint zoneKey)
    {
        return _zones.TryGetValue(zoneKey, out var zone) ? zone : null;
    }

    public ZoneGroup GetZoneGroupById(uint zoneId)
    {
        return _groups.TryGetValue(zoneId, out var group) ? group : null;
    }

    public List<uint> GetZoneKeysInZoneGroupById(uint zoneGroupId)
    {
        var res = new List<uint>();
        foreach (var z in _zones)
            if (z.Value.GroupId == zoneGroupId)
                res.Add(z.Value.ZoneKey);
        return res;
    }

    public uint GetTargetIdByZoneId(uint zoneId)
    {
        var zone = GetZoneByKey(zoneId);
        if (zone == null) return 0;
        var zoneGroup = GetZoneGroupById(zone.GroupId);
        return zoneGroup?.TargetId ?? 0;
    }

    public void Load()
    {
        _zoneIdToKey = [];
        _zones = [];
        _groups = [];
        _conflicts = [];
        _groupBannedTags = [];
        _climateElem = [];
        Logger.Info("Loading ZoneManager...");
        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM zones";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new Zone
                        {
                            Id = reader.GetUInt32("id"),
                            Name = (string)reader.GetValue("name"),
                            ZoneKey = reader.GetUInt32("zone_key"),
                            GroupId = reader.GetUInt32("group_id", 0),
                            Closed = reader.GetBoolean("closed", true),
                            FactionId = (FactionsEnum)reader.GetUInt32("faction_id", 0),
                            ZoneClimateId = reader.GetUInt32("zone_climate_id", 0)
                        };
                        _zoneIdToKey.Add(template.Id, template.ZoneKey);
                        _zones.Add(template.ZoneKey, template);
                    }
                }
            }

            Logger.Info("Loaded {0} zones", _zones.Count);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM zone_groups";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ZoneGroup
                        {
                            Id = reader.GetUInt32("id"), Name = (string)reader.GetValue("name"), X = reader.GetFloat("x"),
                            Y = reader.GetFloat("y"),
                            Width = reader.GetFloat("w"),
                            Hight = reader.GetFloat("h"),
                            TargetId = reader.GetUInt32("target_id"),
                            FactionChatRegionId = reader.GetUInt32("faction_chat_region_id"),
                            PirateDesperado = reader.GetBoolean("pirate_desperado", true),
                            FishingSeaLootPackId = reader.GetUInt32("fishing_sea_loot_pack_id", 0),
                            FishingLandLootPackId = reader.GetUInt32("fishing_land_loot_pack_id", 0),
                            // 1.2 added BuffId
                            BuffId = reader.GetUInt32("buff_id", 0)
                        };
                        _groups.Add(template.Id, template);
                    }
                }
            }

            Logger.Info("Loaded {0} groups", _groups.Count);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM conflict_zones";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var zoneGroupId = reader.GetUInt16("zone_group_id");
                        if (_groups.ContainsKey(zoneGroupId))
                        {
                            var template = new ZoneConflict(_groups[zoneGroupId]) { ZoneGroupId = zoneGroupId };

                            for (var i = 0; i < 5; i++)
                            {
                                template.NumKills[i] = reader.GetInt32($"num_kills_{i}");
                                template.NoKillMin[i] = reader.GetInt32($"no_kill_min_{i}");
                            }

                            template.ConflictMin = reader.GetInt32("conflict_min");
                            template.WarMin = reader.GetInt32("war_min");
                            template.PeaceMin = reader.GetInt32("peace_min");

                            template.PeaceProtectedFactionId = reader.GetUInt32("peace_protected_faction_id", 0);
                            template.NuiaReturnPointId = reader.GetUInt32("nuia_return_point_id", 0);
                            template.HariharaReturnPointId = reader.GetUInt32("harihara_return_point_id", 0);
                            template.WarTowerDefId = reader.GetUInt32("war_tower_def_id", 0);
                            // TODO 1.2 // template.PeaceTowerDefId = reader.GetUInt32("peace_tower_def_id", 0);
                            template.Closed = reader.GetBoolean("closed", true);

                            _groups[zoneGroupId].Conflict = template;
                            _conflicts.Add(zoneGroupId, template);
                        }
                        else
                            Logger.Warn("ZoneGroupId: {0} doesn't exist for conflict", zoneGroupId);
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM zone_group_banned_tags";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ZoneGroupBannedTag
                        {
                            Id = reader.GetUInt32("id"),
                            ZoneGroupId = reader.GetUInt32("zone_group_id"),
                            TagId = reader.GetUInt32("tag_id")
                        };
                        // TODO 1.2 // template.BannedPeriodsId = reader.GetUInt32("banned_periods_id");
                        _groupBannedTags.Add(template.Id, template);
                    }
                }
            }

            Logger.Info("Loaded {0} group banned tags", _groupBannedTags.Count);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM zone_climate_elems";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ZoneClimateElem
                        {
                            Id = reader.GetUInt32("id"),
                            ZoneClimateId = reader.GetUInt32("zone_climate_id"),
                            ClimateId = (Climate)reader.GetUInt32("climate_id")
                        };
                        _climateElem.Add(template.Id, template);
                    }
                }
            }

            Logger.Info("Loaded {0} climate elems", _climateElem.Count);
        }

        using var stateConnection = MySQL.CreateConnection();
        RestoreStates(stateConnection);
    }

    internal void RestoreStates(MySqlConnection connection)
    {
        using var stateCommand = connection.CreateCommand();
        stateCommand.CommandText = "SELECT zone_group_id, state, kill_count, next_state_time FROM zone_conflict_states";
        var savedStates = new Dictionary<ushort, ZoneConflictSnapshot>();
        using (var reader = stateCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                savedStates.Add(reader.GetUInt16("zone_group_id"), new ZoneConflictSnapshot(
                    (ZoneConflictType)reader.GetByte("state"), reader.GetUInt32("kill_count"),
                    reader.IsDBNull(reader.GetOrdinal("next_state_time")) ? DateTime.MinValue :
                        DateTime.SpecifyKind(reader.GetDateTime("next_state_time"), DateTimeKind.Utc)));
            }
        }
        foreach (var conflict in _conflicts.Values)
            conflict.Restore(savedStates.TryGetValue(conflict.ZoneGroupId, out var state) ? state : null);
    }

    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        foreach (var conflict in _conflicts.Values.Where(conflict => !conflict.Closed))
        {
            conflict.CheckTimer();
            taskManager.Schedule(new ZoneStateChangeTask(conflict), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public int Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        foreach (var conflict in _conflicts.Values)
        {
            var state = conflict.GetSnapshot();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO zone_conflict_states (zone_group_id, state, kill_count, next_state_time) " +
                "VALUES (@zone, @state, @kills, @deadline) ON DUPLICATE KEY UPDATE " +
                "state = @state, kill_count = @kills, next_state_time = @deadline";
            command.Parameters.AddWithValue("@zone", conflict.ZoneGroupId);
            command.Parameters.AddWithValue("@state", (byte)state.State);
            command.Parameters.AddWithValue("@kills", state.KillCount);
            command.Parameters.AddWithValue("@deadline", state.NextStateTime == DateTime.MinValue ? DBNull.Value : state.NextStateTime);
            command.ExecuteNonQuery();
        }
        return _conflicts.Count;
    }

    public Vector2 GetZoneOriginCell(uint zoneId)
    {
        var world = worldManager.GetWorldTemplateByZoneKey(zoneId);
        if (world?.XmlWorldZones.TryGetValue(zoneId, out var xmlZone) ?? false)
        {
            return new Vector2(xmlZone.OriginX, xmlZone.OriginY);
        }
        return new Vector2();
    }

    /// <summary>
    /// translate the local coordinates to the world coordinates using the original coordinates of the cells for the zone
    /// </summary>
    /// <param name="zoneId">zoneKey</param>
    /// <param name="point">offset inside the zone</param>
    /// <returns></returns>
    public Vector3 ConvertToWorldCoordinates(uint zoneId, Vector3 point)
    {
        var origin = GetZoneOriginCell(zoneId);

        var newX = origin.X * 1024f + point.X;
        var newY = origin.Y * 1024f + point.Y;

        return new Vector3(newX, newY, point.Z);
    }

    public List<Climate> GetClimatesByZone(Zone zone)
    {
        var res = new List<Climate>();
        foreach (var zoneClimateElem in _climateElem.Values)
        {
            if (zoneClimateElem.ZoneClimateId == zone.ZoneClimateId)
                res.Add(zoneClimateElem.ClimateId);
        }
        return res;
    }

    /// <summary>
    /// Checks if a doodad is located in a matching climate
    /// </summary>
    /// <param name="doodad"></param>
    /// <returns>Returns true if the doodad can have a growth time bonus, false if out of climate, or no climate defined for the doodad</returns>
    public bool DoodadHasMatchingClimate(Doodad doodad)
    {
        // If no climate defined, then don't give a bonus
        if (doodad.Template == null || doodad.Template.ClimateId == Climate.None || doodad.Template.ClimateId == Climate.Any)
            return false;

        // Get doodad's zone (if missing zoneId (key)
        if (doodad.Transform.ZoneId <= 0)
        {
            // If ZoneId wasn't set yet, calculate it
            var zoneId = worldManager.GetZoneId(doodad.ParentWorld.Template, doodad.Transform.World.Position.X, doodad.Transform.World.Position.Y);
            doodad.Transform.ZoneId = zoneId;
        }
        var zone = GetZoneByKey(doodad.Transform.ZoneId);
        if (zone == null)
            return false;

        // Get the climates list for this zone
        var zoneClimates = GetClimatesByZone(zone);

        // Check if it's in there
        return zoneClimates.Contains(doodad.Template.ClimateId);
    }

    public bool IsPirateDesperadoZone(uint zoneKey)
    {
        var zone = GetZoneByKey(zoneKey);
        if (zone == null)
            return false;
        var group = GetZoneGroupById(zone.GroupId);
        if (group == null)
            return false;
        return group.PirateDesperado;
    }
}
