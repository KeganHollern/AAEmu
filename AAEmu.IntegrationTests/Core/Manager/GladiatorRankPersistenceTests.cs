using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.InstantGame;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class GladiatorRankPersistenceTests
{
    [Fact]
    public void LostAcknowledgementAndNewConnection_RetainOneCompleteResultPerMatch()
    {
        var original = Result();
        using (var first = MySQL.CreateConnection())
            original.Write(first);
        // Reopen the database after a commit whose acknowledgement was lost.
        using var reconnect = MySQL.CreateConnection();
        original.Write(reconnect);
        Assert.Throws<InvalidDataException>(() => (original with { Corps1RankPoints = 99 }).Write(reconnect));
        var next = original with { MatchId = Guid.NewGuid(), Corps1Score = 0, Corps2Score = 60,
            Corps1RankPoints = 1, Corps2RankPoints = 4 };
        next.Write(reconnect);
        using var read = reconnect.CreateCommand();
        read.CommandText = """
            SELECT COUNT(*), SUM(corps1_rank_points), SUM(corps2_rank_points),
                   SUM(corps1_score > corps2_score), SUM(corps2_score > corps1_score)
            FROM gladiator_rank_results WHERE match_id IN (@first,@next)
              AND corps1_character_id=@red AND corps2_character_id=@blue
            """;
        read.Parameters.AddWithValue("@first", original.MatchId.ToString());
        read.Parameters.AddWithValue("@next", next.MatchId.ToString());
        read.Parameters.AddWithValue("@red", original.Corps1CharacterId);
        read.Parameters.AddWithValue("@blue", original.Corps2CharacterId);
        using var rows = read.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(2, rows.GetInt32(0));
        Assert.Equal(5, rows.GetInt32(1));
        Assert.Equal(5, rows.GetInt32(2));
        Assert.Equal(1, rows.GetInt32(3));
        Assert.Equal(1, rows.GetInt32(4));
    }

    [Fact]
    public async Task FailedWriteAndCheckpoint_PersistBothPlayersAtTheNormalShutdownCheckpoint()
    {
        var scheduler = new TaskManager(Mock.Of<ITickManager>(ticks => ticks.OnTick == new TickManager.TickEventHandler()));
        var graph = new EconomyPersistenceTests.SaveGraph(scheduler);
        var manager = InstantGameManager.Instance;
        var result = Result();
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        var original = LogManager.Configuration;
        using var target = new MemoryTarget { Layout = "${message}" };
        var config = new LoggingConfiguration();
        config.AddRule(LogLevel.Error, LogLevel.Fatal, target, typeof(InstantGameManager).FullName);
        var trigger = $"arena_rank_failure_{Guid.NewGuid():N}";
        command.CommandText = $"CREATE TRIGGER {trigger} BEFORE INSERT ON gladiator_rank_results FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='private failed result'";
        command.ExecuteNonQuery();
        Task saved = null;
        try
        {
            LogManager.Configuration = config;
            lock (SaveManager.PersistenceSyncRoot)
                saved = manager.RecordRankResult(result);
            Assert.True(SpinWait.SpinUntil(() => target.Logs.Count > 0, TimeSpan.FromSeconds(10)));
            Assert.False(graph.Save.DoSave());
            Assert.False(saved.IsCompleted);
            command.CommandText = "SELECT COUNT(*) FROM gladiator_rank_results WHERE match_id=@id";
            command.Parameters.AddWithValue("@id", result.MatchId.ToString());
            Assert.Equal(0L, command.ExecuteScalar());
        }
        finally
        {
            command.CommandText = $"DROP TRIGGER IF EXISTS {trigger}";
            command.ExecuteNonQuery();
            LogManager.Configuration = original;
        }

        var singleton = typeof(Singleton<TaskManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        singleton.SetValue(null, scheduler);
        try
        {
            graph.Save.SaveTickStart();
            await graph.Save.StopAsync();
        }
        finally
        {
            scheduler.Stop();
            singleton.SetValue(null, previous);
        }
        await saved.WaitAsync(TimeSpan.FromSeconds(5));
        using var restarted = MySQL.CreateConnection();
        using var read = restarted.CreateCommand();
        read.CommandText = "SELECT corps1_rank_points,corps2_rank_points FROM gladiator_rank_results WHERE match_id=@id";
        read.Parameters.AddWithValue("@id", result.MatchId.ToString());
        using var rows = read.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal((4, 1), (rows.GetInt32(0), rows.GetInt32(1)));
        Assert.False(rows.Read());
    }

    private static GladiatorRankResult Result() =>
        new(Guid.NewGuid(), new DateTime(2026, 11, 25, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234560), 1100001, 60, 4, 1100002, 30, 1);
}
