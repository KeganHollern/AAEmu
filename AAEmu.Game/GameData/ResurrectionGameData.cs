using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;
using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class ResurrectionGameData : Singleton<ResurrectionGameData>, IGameDataLoader
{
    private readonly SortedDictionary<int, ResurrectionWaitingTime> _waitingTimes = [];

    public ResurrectionWaitingTime Get(int deathCount)
    {
        if (_waitingTimes.Count == 0)
            throw new InvalidOperationException("Resurrection waiting times are not loaded");
        return _waitingTimes[Math.Clamp(deathCount, 1, _waitingTimes.Count)];
    }

    public void Load(SqliteConnection connection)
    {
        _waitingTimes.Clear();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, penalty_duration, waiting_time, siege_waiting_time FROM resurrection_waiting_times ORDER BY id";
        using var sqlite = command.ExecuteReader();
        using var reader = new SQLiteWrapperReader(sqlite);
        while (reader.Read())
        {
            var row = new ResurrectionWaitingTime(reader.GetInt32("id"),
                checked(reader.GetInt32("penalty_duration") * 1000),
                checked(reader.GetInt32("waiting_time") * 1000),
                checked(reader.GetInt32("siege_waiting_time") * 1000));
            if (row.DeathCount != _waitingTimes.Count + 1 || row.PenaltyMilliseconds < 0 ||
                row.WaitMilliseconds < 0 || row.SiegeWaitMilliseconds < 0)
                throw new InvalidDataException("Invalid resurrection waiting time");
            _waitingTimes.Add(row.DeathCount, row);
        }
        if (_waitingTimes.Count == 0)
            throw new InvalidDataException("Missing resurrection waiting times");
    }

    public void PostLoad() { }
}

public sealed record ResurrectionWaitingTime(int DeathCount, int PenaltyMilliseconds,
    int WaitMilliseconds, int SiegeWaitMilliseconds);
