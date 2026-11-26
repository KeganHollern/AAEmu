using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.InstantGame;

internal sealed record ArenaHonorAward(uint CharacterId, int Amount)
{
    // The persistence gate protects the loaded balance and holds it through commit.
    internal static void ClaimPending(PersistenceSaveContext context, IReadOnlyCollection<Character> characters, List<Character> credited)
    {
        var players = characters.Where(player => !player.IsDeleted).ToDictionary(player => player.Id);
        if (players.Count == 0)
            return;
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        var keys = players.Keys.Select((characterId, i) => command.Parameters.AddWithValue($"@p{i}", characterId).ParameterName).ToArray();
        command.CommandText = $"""
            SELECT match_id,character_id,honor FROM arena_match_honor
            WHERE claimed=0 AND character_id IN ({string.Join(",", keys)})
            ORDER BY character_id,match_id FOR UPDATE
            """;
        var pending = new List<(string MatchId, uint CharacterId, int Honor)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
                pending.Add((rows.GetGuid(0).ToString("D"), rows.GetUInt32(1), rows.GetInt32(2)));

        command.Parameters.Clear();
        var id = command.Parameters.Add("@id", MySqlDbType.UInt32);
        var match = command.Parameters.Add("@match", MySqlDbType.VarChar);
        var balance = command.Parameters.Add("@balance", MySqlDbType.Int32);
        foreach (var group in pending.GroupBy(row => row.CharacterId))
        {
            var player = players[group.Key];
            var total = group.Sum(row => (long)row.Honor);
            if (total > int.MaxValue - (long)player.HonorPoint || total > int.MaxValue)
                continue; // Retain the awards until the balance has room.
            id.Value = player.Id;
            var updated = player.HonorPoint + (int)total;
            balance.Value = updated;
            command.CommandText = "UPDATE characters SET honor_point=@balance WHERE id=@id AND deleted=0";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException($"Missing arena Honor recipient {player.Id}.");
            command.CommandText = "UPDATE arena_match_honor SET claimed=1 WHERE character_id=@id AND match_id=@match AND claimed=0";
            foreach (var row in group)
            {
                match.Value = row.MatchId;
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidDataException($"Missing arena Honor receipt {row.MatchId} for {player.Id}.");
            }
            context.AfterCommit(() =>
            {
                player.HonorPoint = updated;
                credited.Add(player);
            });
        }
    }
}
