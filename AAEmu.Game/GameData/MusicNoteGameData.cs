using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class MusicNoteGameData : Singleton<MusicNoteGameData>, IGameDataLoader
{
    private Dictionary<byte, int> _limits = [];

    public void Load(SqliteConnection connection)
    {
        var limits = new Dictionary<byte, int>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT step, note_length FROM music_note_limits";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var step = reader.GetInt32(0);
            var length = reader.GetInt32(1);
            if (step is < byte.MinValue or > byte.MaxValue || length <= 0 ||
                !limits.TryAdd((byte)step, length))
                throw new InvalidDataException("Invalid or duplicate music_note_limits step.");
        }

        if (!limits.ContainsKey(0))
            throw new InvalidDataException("music_note_limits has no novice limit.");
        Volatile.Write(ref _limits, limits);
    }

    public bool TryGetLimit(byte step, out int limit)
    {
        return Volatile.Read(ref _limits).TryGetValue(step, out limit);
    }

    public void PostLoad() { }
}
