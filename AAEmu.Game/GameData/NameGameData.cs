using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class NameGameData : Singleton<NameGameData>, IGameDataLoader
{
    internal sealed record BlockedName(string Text, bool PartialMatch);
    internal sealed record Snapshot(FrozenSet<int> AllowedCharacters, ImmutableArray<BlockedName> BlockedNames);

    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private Snapshot _snapshot = new(FrozenSet<int>.Empty, []);

    internal Snapshot Data => Volatile.Read(ref _snapshot);

    public void Load(SqliteConnection connection)
    {
        var allowedCharacters = new HashSet<int>();
        var allowedIds = new HashSet<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, char, bytes FROM allowed_name_chars";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var text = reader.GetString(1);
                if (!allowedIds.Add(reader.GetInt32(0)) || !ValidText(text, reader.GetInt32(2)) ||
                    !Rune.TryGetRuneAt(text, 0, out var character) || character.Utf16SequenceLength != text.Length ||
                    !allowedCharacters.Add(character.Value))
                    throw new InvalidDataException("Invalid or duplicate allowed_name_chars row.");
            }
        }

        var blockedNames = ImmutableArray.CreateBuilder<BlockedName>();
        var blockedIds = new HashSet<int>();
        using (var command = connection.CreateCommand())
        {
            // Chat filtering is separate. Do not load or apply check_chat rows here.
            command.CommandText = "SELECT id, utf8str, bytes, partial_match FROM blocked_texts WHERE check_name = 't'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var text = reader.GetString(1);
                var partialMatch = reader.GetString(3);
                if (!blockedIds.Add(reader.GetInt32(0)) || !ValidText(text, reader.GetInt32(2)) ||
                    partialMatch is not ("t" or "f"))
                    throw new InvalidDataException("Invalid or duplicate blocked_texts name row.");
                blockedNames.Add(new BlockedName(text, partialMatch == "t"));
            }
        }

        Volatile.Write(ref _snapshot, new Snapshot(allowedCharacters.ToFrozenSet(), blockedNames.ToImmutable()));
    }

    private static bool ValidText(string text, int bytes)
    {
        if (string.IsNullOrEmpty(text) || text.Contains('\0'))
            return false;
        try
        {
            return s_utf8.GetByteCount(text) == bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public void PostLoad() { }
}
