using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;
using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class PremiumGameData : Singleton<PremiumGameData>, IGameDataLoader
{
    // These initial values also support consumers constructed before the data load.
    private Dictionary<uint, PremiumBenefits> _benefits = new()
    {
        [1] = new(5, 0, 2000), [2] = new(10, 5, 5000)
    };
    private Dictionary<uint, int> _points = new() { [1] = 0, [2] = 1 };
    public int GetPoint(bool patron) => _points[patron ? 2u : 1u];
    public PremiumBenefits Get(bool patron) => _benefits[patron ? 2u : 1u];

    public void Load(SqliteConnection connection)
    {
        var benefits = new Dictionary<uint, PremiumBenefits>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT grade_id, online_labor, offline_labor, max_labor FROM premium_benefits";
        using var sqlite = command.ExecuteReader();
        using var reader = new SQLiteWrapperReader(sqlite);
        while (reader.Read())
        {
            var row = new PremiumBenefits(reader.GetInt32("online_labor"), reader.GetInt32("offline_labor"), reader.GetInt32("max_labor"));
            if (row.OnlineLabor < 0 || row.OfflineLabor < 0 || row.MaxLabor is < 1 or > short.MaxValue)
                throw new InvalidDataException("Invalid premium benefits");
            benefits.Add(reader.GetUInt32("grade_id"), row);
        }
        if (!benefits.ContainsKey(1) || !benefits.ContainsKey(2))
            throw new InvalidDataException("The non-patron and patron benefit rows are required");
        sqlite.Close();
        command.CommandText = "SELECT grade_id, point FROM premium_grades";
        using var grades = command.ExecuteReader();
        var points = new Dictionary<uint, int>();
        while (grades.Read())
        {
            var point = grades.GetInt32(1);
            if (point < 0)
                throw new InvalidDataException("Invalid premium grade point");
            points.Add(checked((uint)grades.GetInt64(0)), point);
        }
        if (!points.ContainsKey(1) || !points.ContainsKey(2))
            throw new InvalidDataException("The non-patron and patron point rows are required");
        _benefits = benefits;
        _points = points;
    }
    public void PostLoad() { }
}

public sealed record PremiumBenefits(int OnlineLabor, int OfflineLabor, int MaxLabor);
