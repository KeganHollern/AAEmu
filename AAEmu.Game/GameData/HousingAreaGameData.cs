using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class HousingAreaGameData : Singleton<HousingAreaGameData>, IGameDataLoader
{
    private readonly Dictionary<uint, HousingAreas> _areas = [];
    private readonly Dictionary<uint, HousingGroup> _groups = [];

    public int AreaCount => _areas.Count;
    public int GroupCount => _groups.Count;
    public HousingAreas GetArea(uint id) => _areas.GetValueOrDefault(id);
    public HousingGroup GetGroup(uint id) => _groups.GetValueOrDefault(id);

    public void Load(SqliteConnection connection)
    {
        _areas.Clear();
        _groups.Clear();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM housing_areas";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var area = new HousingAreas
                {
                    Id = reader.GetUInt32("id"),
                    Name = reader.GetString("name"),
                    GroupId = reader.GetUInt32("housing_group_id")
                };
                _areas.Add(area.Id, area);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM housing_groups";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var group = new HousingGroup
                {
                    Id = reader.GetUInt32("id"),
                    Name = reader.GetString("name"),
                    Description = reader.GetString("desc"),
                    DoodadId = reader.GetUInt32("doodad_id", 0),
                    Houseless = reader.GetBoolean("houseless", true),
                    ExistingCategoryId = reader.GetUInt32("existing_category_id", 0),
                    AllowedTaxDelayWeek = reader.GetInt32("allowed_tax_delay_week"),
                    CanExtend = reader.GetBoolean("can_extend", true)
                };
                _groups.Add(group.Id, group);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM housing_group_categories";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var groupId = reader.GetUInt32("housing_group_id");
                if (!_groups.TryGetValue(groupId, out var group))
                    throw new InvalidDataException($"Housing category references missing group {groupId}.");
                group.CategoryLimits.Add(reader.GetUInt32("category_id"), reader.GetUInt32("max_construct_count"));
            }
        }
        foreach (var area in _areas.Values)
            if (!_groups.ContainsKey(area.GroupId))
                throw new InvalidDataException($"Housing area {area.Id} references missing group {area.GroupId}.");
    }

    public void PostLoad() { }
}
