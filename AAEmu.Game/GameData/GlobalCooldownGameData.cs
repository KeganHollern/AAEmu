using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class GlobalCooldownGameData : Singleton<GlobalCooldownGameData>, IGameDataLoader
{
    private double _minimum = -800;
    private double _maximum = 4000;

    public float GetMultiplier(double bonus)
    {
        var clamped = Math.Clamp(double.IsFinite(bonus) ? bonus : 0, _minimum, _maximum);
        return (float)(100000.0 / (clamped + 1000.0));
    }

    public void Load(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT minimum, maximum FROM unit_attribute_limits WHERE unit_attribute_id=74";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("The global cooldown attribute limit is required.");
        var minimum = reader.GetDouble(0);
        var maximum = reader.GetDouble(1);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum <= -1000 || minimum > maximum)
            throw new InvalidDataException("Invalid global cooldown attribute limit.");
        if (reader.Read())
            throw new InvalidDataException("Duplicate global cooldown attribute limit.");
        _minimum = minimum;
        _maximum = maximum;
    }

    public void PostLoad() { }
}
