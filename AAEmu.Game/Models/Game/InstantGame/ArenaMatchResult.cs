using AAEmu.Game.Core.Managers;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.InstantGame;

internal sealed record ArenaMatchResult(Guid MatchId, uint BattlefieldId, GladiatorRankResult Rank,
    IReadOnlyList<ArenaHonorAward> Awards)
{
    public void Write(PersistenceSaveContext context)
    {
        Rank?.Write(context.Connection, context.Transaction);
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.Parameters.AddWithValue("@match", MatchId.ToString("D"));
        command.Parameters.AddWithValue("@field", BattlefieldId);
        var id = command.Parameters.Add("@id", MySqlDbType.UInt32);
        var honor = command.Parameters.Add("@honor", MySqlDbType.Int32);
        foreach (var award in Awards)
        {
            id.Value = award.CharacterId;
            honor.Value = award.Amount;
            command.CommandText = """
                INSERT INTO arena_match_honor (match_id,character_id,battlefield_id,honor)
                VALUES (@match,@id,@field,@honor)
                """;
            try
            {
                command.ExecuteNonQuery();
            }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                // A retry must preserve the original amount and its paid state.
                command.CommandText = """
                    SELECT 1 FROM arena_match_honor
                    WHERE match_id=@match AND character_id=@id AND battlefield_id=@field AND honor=@honor
                    """;
                if (command.ExecuteScalar() == null)
                    throw new InvalidDataException($"Conflicting arena Honor for {MatchId}, character {award.CharacterId}.", ex);
            }
        }
    }
}
