using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Units;
using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class ExperienceModifierGameData : Singleton<ExperienceModifierGameData>, IGameDataLoader
{
    private Dictionary<UnitAttribute, (double Minimum, double Maximum)> _limits = new()
    {
        [UnitAttribute.ExpMul] = (0, 500)
    };

    public double Clamp(UnitAttribute attribute, double value) => _limits.TryGetValue(attribute, out var limit)
        ? Math.Clamp(value, limit.Minimum, limit.Maximum) : value;

    public void Load(SqliteConnection connection)
    {
        var limits = new Dictionary<UnitAttribute, (double, double)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT unit_attribute_id, minimum, maximum FROM unit_attribute_limits WHERE unit_attribute_id IN (95,186)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var minimum = reader.GetDouble(1);
            var maximum = reader.GetDouble(2);
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
                throw new InvalidDataException("Invalid experience attribute limit");
            limits.Add((UnitAttribute)checked((uint)reader.GetInt64(0)), (minimum, maximum));
        }
        if (!limits.ContainsKey(UnitAttribute.ExpMul))
            throw new InvalidDataException("The ExpMul attribute limit is required");
        _limits = limits;
    }

    public void PostLoad() { }
}
