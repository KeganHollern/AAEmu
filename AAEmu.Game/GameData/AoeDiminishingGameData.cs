using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class AoeDiminishingGameData : Singleton<AoeDiminishingGameData>, IGameDataLoader
{
    private readonly Dictionary<int, float> _rates = [];

    public void Load(SqliteConnection connection)
    {
        _rates.Clear();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, rate FROM aoe_diminishings ORDER BY id";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
            _rates.Add(reader.GetInt32("id"), reader.GetFloat("rate") / 100f);
    }

    public void PostLoad() { }

    public float GetMultiplier(int targetNumber)
    {
        // The authored IDs are 1..10. Keep the final authored rate for larger groups.
        return _rates.Count == 0 ? 1f : _rates[Math.Clamp(targetNumber, 1, _rates.Count)];
    }
}
