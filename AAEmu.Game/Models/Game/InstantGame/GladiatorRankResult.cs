using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.InstantGame;

// Both corps share one immutable result. Character IDs remain stable across logins.
internal sealed record GladiatorRankResult(Guid MatchId, DateTime CompletedAt,
    uint Corps1CharacterId, int Corps1Score, int Corps1RankPoints,
    uint Corps2CharacterId, int Corps2Score, int Corps2RankPoints)
{
    public void Write(MySqlConnection connection, MySqlTransaction transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gladiator_rank_results
                (match_id, completed_at, corps1_character_id, corps1_score, corps1_rank_points,
                 corps2_character_id, corps2_score, corps2_rank_points)
            VALUES (@id, @completed, @red, @redScore, @redPoints, @blue, @blueScore, @bluePoints)
            """;
        command.Parameters.AddWithValue("@id", MatchId.ToString("D"));
        command.Parameters.AddWithValue("@completed", CompletedAt);
        command.Parameters.AddWithValue("@red", Corps1CharacterId);
        command.Parameters.AddWithValue("@redScore", Corps1Score);
        command.Parameters.AddWithValue("@redPoints", Corps1RankPoints);
        command.Parameters.AddWithValue("@blue", Corps2CharacterId);
        command.Parameters.AddWithValue("@blueScore", Corps2Score);
        command.Parameters.AddWithValue("@bluePoints", Corps2RankPoints);
        try
        {
            command.ExecuteNonQuery();
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            // A lost commit acknowledgement can retry the same immutable result.
            command.CommandText = """
                SELECT 1 FROM gladiator_rank_results WHERE match_id=@id AND completed_at=@completed
                  AND corps1_character_id=@red AND corps1_score=@redScore AND corps1_rank_points=@redPoints
                  AND corps2_character_id=@blue AND corps2_score=@blueScore AND corps2_rank_points=@bluePoints
                """;
            if (command.ExecuteScalar() == null)
                throw new InvalidDataException($"Gladiator result {MatchId} already has a different outcome.", ex);
        }
    }
}
