using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class BattlefieldGameData : Singleton<BattlefieldGameData>, IGameDataLoader
{
    private Dictionary<uint, Battlefield> _battlefields;

    public Battlefield GetBattlefield(uint id)
    {
        return _battlefields.GetValueOrDefault(id);
    }

    public void Load(SqliteConnection connection)
    {
        _battlefields = new Dictionary<uint, Battlefield>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM battle_fields";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var bf = new Battlefield
                    {
                        Id = reader.GetUInt32("id"),
                        ZoneKey = reader.GetUInt32("zone_key")
                    };

                    _battlefields.Add(bf.Id, bf);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT rules.*, score.event_score AS enemy_player_kill_score,
                    assist.event_score AS enemy_player_damage_assist_score
                FROM game_rule_sets rules
                LEFT JOIN game_score_rules score ON score.rule_set_id = rules.id
                    AND score.rule_set_corps = -1 AND score.event_id = 200 AND score.event_value = 0
                LEFT JOIN game_score_rules assist ON assist.rule_set_id = rules.id
                    AND assist.rule_set_corps = -1 AND assist.event_id = 204 AND assist.event_value = 0
                """;
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var gsr = new GameRuleSet
                    {
                        Id = reader.GetUInt32("id"),
                        BattlefieldId = reader.GetUInt32("battle_field_id"),
                        MinimumLevel = reader.GetUInt32("level_min"),
                        CorpsSize = reader.GetUInt32("corps_size"),
                        Corps1FactionId = reader.GetUInt32("corps1_id"),
                        Corps2FactionId = reader.GetUInt32("corps2_id"),
                        TimeOpening = reader.GetInt32("time_opening"),
                        TimeEnding = reader.GetInt32("time_ending"),
                        TimePlaying = reader.GetInt32("time_playing"),
                        ResurrectionDelay = TimeSpan.FromSeconds(reader.GetUInt32("time_resurrection_delay")),
                        EnemyPlayerKillScore = reader.GetInt32("enemy_player_kill_score", 0),
                        EnemyPlayerDamageAssistScore = reader.GetInt32("enemy_player_damage_assist_score", 0),
                        RankWinPoints = reader.GetInt32("rank_win_point"),
                        RankLossPoints = reader.GetInt32("rank_lose_point"),
                        RankDrawPoints = reader.GetInt32("rank_draw_point"),
                        WinnerHonor = reader.GetInt32("bonus_winner"),
                        LoserHonor = reader.GetInt32("bonus_loser"),
                        TopPlayerKillsHonor = reader.GetInt32("bonus_top_enemy_pc_kill"),
                        TopNpcKillsHonor = reader.GetInt32("bonus_top_enemy_non_pc_kill"),
                        VictoryScore = reader.GetInt32("victory_score")
                    };

                    if (_battlefields.TryGetValue(gsr.BattlefieldId, out var battlefield))
                        battlefield.RuleSet = gsr;
                }
            }
        }

        var pathFile = Path.Combine(FileManager.AppPath, "Data", "battlefields.json");
        var contents = FileManager.GetFileContents(pathFile);
        if (string.IsNullOrWhiteSpace(contents))
            throw new IOException($"File {pathFile} doesn't exists or is empty.");

        if (JsonHelper.TryDeserializeObject(contents, out List<BattlefieldSpawns> bfSpawns, out _))
        {
            foreach (var bfSpawn in bfSpawns)
            {
                if (!_battlefields.TryGetValue(bfSpawn.BattlefieldId, out var battlefield))
                    return;

                battlefield.Spawns = bfSpawn;
            }
        }
    }

    public void PostLoad()
    {
    }
}
