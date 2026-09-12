using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.CommonFarm;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class CommonFarmGameData : Singleton<CommonFarmGameData>, IGameDataLoader
{
    private Dictionary<uint, FarmGroup> _farmGroup = [];
    private Dictionary<uint, FarmGroupDoodads> _farmGroupDoodads = [];
    private Dictionary<uint, DoodadGroups> _doodadGroups = [];
    private readonly Dictionary<uint, CommonFarm> _farms = [];
    private readonly Dictionary<uint, FarmType> _subzoneGroups = [];
    private readonly Dictionary<FarmType, uint> _guardTimes = [];

    public int FarmCount => _farms.Count;
    public CommonFarm GetFarm(uint id) => _farms.GetValueOrDefault(id);
    public FarmType GetSubzoneFarmGroup(uint id) => _subzoneGroups.GetValueOrDefault(id);
    public uint GetGuardTimeMilliseconds(FarmType group) => _guardTimes.GetValueOrDefault(group);
    public bool IsRemovedByHouse(uint doodadGroupId) =>
        _doodadGroups.TryGetValue(doodadGroupId, out var group) && group.RemovedByHouse;

    public void Load(SqliteConnection connection)
    {
        _farmGroup = [];
        _farmGroupDoodads = [];
        _doodadGroups = [];
        _farms.Clear();
        _subzoneGroups.Clear();
        _guardTimes.Clear();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM farm_groups";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var template = new FarmGroup { Id = reader.GetUInt32("id"), Count = reader.GetUInt32("count") };

                _farmGroup.TryAdd(template.Id, template);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, farm_group_id, guard_time FROM common_farms ORDER BY id";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var farm = new CommonFarm
                {
                    Id = reader.GetUInt32("id"), Name = reader.GetString("name"),
                    Group = (FarmType)reader.GetUInt32("farm_group_id"),
                    GuardTimeMilliseconds = reader.GetUInt32("guard_time")
                };
                if (!_farmGroup.ContainsKey((uint)farm.Group))
                    throw new InvalidDataException($"Common farm {farm.Id} references an absent farm group.");
                if (_guardTimes.TryGetValue(farm.Group, out var guardTime) && guardTime != farm.GuardTimeMilliseconds)
                    throw new InvalidDataException($"Common farm group {farm.Group} has conflicting guard times.");
                _guardTimes[farm.Group] = farm.GuardTimeMilliseconds;
                _farms.Add(farm.Id, farm);
            }
        }

        // r208022 has no populated common_farm.xml geometry or subzone foreign key.
        // Its authored subzone names identify farm groups unambiguously. Do not use
        // stale common_farms comments as location geometry or embed subzone IDs.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT s.id, f.farm_group_id FROM sub_zones s JOIN common_farms f ON f.name = s.name GROUP BY s.id, f.farm_group_id";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var id = reader.GetUInt32("id");
                var group = (FarmType)reader.GetUInt32("farm_group_id");
                if (!_subzoneGroups.TryAdd(id, group))
                    throw new InvalidDataException($"Subzone {id} has ambiguous common farm groups.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM farm_group_doodads";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var template = new FarmGroupDoodads
                {
                    Id = reader.GetUInt32("id"),
                    FarmGroupId = (FarmType)reader.GetUInt32("farm_group_id"),
                    DoodadId = reader.GetUInt32("doodad_id"),
                    ItemId = reader.GetUInt32("item_id")
                };

                _farmGroupDoodads.TryAdd(template.Id, template);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM doodad_groups";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var template = new DoodadGroups
                {
                    Id = reader.GetUInt32("id"),
                    GuardOnFieldTime = reader.GetUInt32("guard_on_field_time"),
                    IsExport = reader.GetBoolean("is_export", true),
                    RemovedByHouse = reader.GetBoolean("removed_by_house", true)
                };

                _doodadGroups.TryAdd(template.Id, template);
            }
        }
    }

    public uint GetFarmGroupMaxCount(FarmType farmType)
    {
        return _farmGroup.TryGetValue((uint)farmType, out var farm) ? farm.Count : 0;
    }

    public uint GetDoodadGuardTime(uint groupId)
    {
        return _doodadGroups.TryGetValue(groupId, out var farm) ? farm.GuardOnFieldTime : 0;
    }

    public List<uint> GetAllowedDoodads(FarmType farmType)
    {
        return (from item in _farmGroupDoodads
                where item.Value.FarmGroupId == farmType
                select item.Value.DoodadId).ToList();
    }

    public void PostLoad()
    {
    }
}
