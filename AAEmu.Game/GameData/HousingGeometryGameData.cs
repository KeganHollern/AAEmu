using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class HousingGeometryGameData : Singleton<HousingGeometryGameData>, IGameDataLoader
{
    private Dictionary<(uint ModelId, uint StateId), string[]> _modelPaths = [];

    public void Load(SqliteConnection connection)
    {
        var paths = new Dictionary<(uint, uint), List<string>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.id AS model_id, p.state_id, p.file_path FROM models m " +
            "JOIN prefab_elements p ON p.prefab_model_id=m.sub_id WHERE m.sub_type='PrefabModel' ORDER BY m.id,p.state_id,p.id";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var key = (reader.GetUInt32("model_id"), reader.GetUInt32("state_id"));
            if (!paths.TryGetValue(key, out var elements))
                paths.Add(key, elements = []);
            elements.Add(reader.GetString("file_path"));
        }
        _modelPaths = paths.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public IReadOnlyList<string> GetModelPaths(uint modelId, uint stateId)
    {
        return _modelPaths.TryGetValue((modelId, stateId), out var paths) ? paths : [];
    }

    public void PostLoad() { }
}
