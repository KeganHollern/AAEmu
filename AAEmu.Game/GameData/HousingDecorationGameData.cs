using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class HousingDecorationGameData : Singleton<HousingDecorationGameData>, IGameDataLoader
{
    private readonly Dictionary<uint, string> _groups = [];
    private readonly Dictionary<uint, Dictionary<uint, uint>> _limits = [];

    public int GroupCount => _groups.Count;
    public int LimitCount => _limits.Count;

    public uint GetLimit(uint limitId, uint groupId) =>
        _limits.TryGetValue(limitId, out var limits) ? limits.GetValueOrDefault(groupId) : 0;

    public void Load(SqliteConnection connection)
    {
        _groups.Clear();
        _limits.Clear();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name FROM deco_actability_groups";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
                _groups.Add(reader.GetUInt32("id"), reader.GetString("name"));
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM housing_deco_limits";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
                _limits.Add(reader.GetUInt32("id"), []);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT housing_deco_limit_id, deco_actability_group_id, count FROM housing_deco_limit_elems";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var limitId = reader.GetUInt32("housing_deco_limit_id");
                var groupId = reader.GetUInt32("deco_actability_group_id");
                if (!_limits.TryGetValue(limitId, out var limits) || !_groups.ContainsKey(groupId))
                    throw new InvalidDataException($"Invalid housing decoration limit {limitId}/{groupId}.");
                limits.Add(groupId, reader.GetUInt32("count"));
            }
        }
    }

    public void PostLoad() { }
}
