using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class UnitAttributeLimitsGameData : Singleton<UnitAttributeLimitsGameData>, IGameDataLoader
{
    private Dictionary<UnitAttribute, (double Minimum, double Maximum)> _limits = [];

    public double Clamp(UnitAttribute attribute, double value)
    {
        return _limits.TryGetValue(attribute, out var limit)
            ? Math.Clamp(value, limit.Minimum, limit.Maximum)
            : value;
    }

    public void Load(SqliteConnection connection)
    {
        var limits = new Dictionary<UnitAttribute, (double Minimum, double Maximum)>();
        using var command = connection.CreateCommand();
        // XP and GCD retain their existing loaders and unit conversions.
        command.CommandText = "SELECT unit_attribute_id, minimum, maximum FROM unit_attribute_limits WHERE unit_attribute_id NOT IN (74,95,186)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var attributeId = reader.GetInt32(0);
            var minimum = reader.GetDouble(1);
            var maximum = reader.GetDouble(2);
            if (attributeId is < 0 or > byte.MaxValue || !Enum.IsDefined((UnitAttribute)attributeId) ||
                !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
                throw new InvalidDataException($"Invalid unit attribute limit for {attributeId}.");
            if (!limits.TryAdd((UnitAttribute)attributeId, (minimum, maximum)))
                throw new InvalidDataException($"Duplicate unit attribute limit for {attributeId}.");
        }

        _limits = limits;
    }

    public void PostLoad() { }
}
