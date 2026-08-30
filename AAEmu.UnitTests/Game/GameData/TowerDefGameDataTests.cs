using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.TowerDefs;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public class TowerDefGameDataTests
{
    [Test]
    public async Task Load_OrphanRowsBeforeValidDefinition_DoesNotTruncateValidGraph()
    {
        using var connection = CreateConnection();
        Execute(connection, """
                            INSERT INTO tower_defs VALUES
                            (3, 'Crimson', 'start', 'end', 12, 0, 9846, NULL, 0, 3600, 1, 'Crimson Rift', 5);
                            INSERT INTO tower_def_progs VALUES
                            (1, 22, 'orphan', 10, 't'),
                            (2, 3, 'wave', 0, 't');
                            INSERT INTO tower_def_prog_spawn_targets VALUES
                            (1, 99, 9999, 'NpcSpawner', 'f'),
                            (2, 2, 9848, 'NpcSpawner', 'f');
                            INSERT INTO tower_def_prog_kill_targets VALUES
                            (1, 99, 111, 'Npc', 1),
                            (2, 2, 8834, 'Npc', 23);
                            """);
        var data = new TowerDefGameData();

        data.Load(connection);
        data.PostLoad();

        var definition = data.Get(3);
        await Assert.That(definition).IsNotNull();
        await Assert.That(definition.IsValid).IsTrue();
        await Assert.That(definition.Progs).Count().IsEqualTo(1);
        await Assert.That(definition.Progs[0].StepOrdinal).IsEqualTo(0u);
        await Assert.That(definition.Progs[0].SpawnTargets[0].SpawnTargetType)
            .IsEqualTo(TowerDefTargetType.NpcSpawner);
        await Assert.That(definition.Progs[0].KillTargets[0].KillTargetType)
            .IsEqualTo(TowerDefTargetType.Npc);
        await Assert.That(data.ValidationIssues.Count(issue => issue.Severity == TowerDefValidationSeverity.Warning))
            .IsEqualTo(3);
    }

    [Test]
    public async Task PostLoad_UnsupportedTargetType_DisablesOnlyAffectedDefinition()
    {
        using var connection = CreateConnection();
        Execute(connection, """
                            INSERT INTO tower_defs VALUES
                            (3, 'Valid', '', '', 12, 0, 9846, NULL, 0, 3600, 1, '', 5),
                            (5, 'Invalid', '', '', 12, 0, 8939, NULL, 0, 3600, 1, '', 5);
                            INSERT INTO tower_def_progs VALUES
                            (2, 3, '', 10, 't'),
                            (3, 5, '', 10, 't');
                            INSERT INTO tower_def_prog_spawn_targets VALUES
                            (2, 2, 9848, 'NpcSpawner', 'f'),
                            (3, 3, 8940, 'UnknownTarget', 'f');
                            """);
        var data = new TowerDefGameData();

        data.Load(connection);
        data.PostLoad();

        await Assert.That(data.Get(3).IsValid).IsTrue();
        await Assert.That(data.Get(5).IsValid).IsFalse();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.TowerDefId == 5 && issue.Severity == TowerDefValidationSeverity.Error)).IsTrue();
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
                            CREATE TABLE tower_defs (
                                id INTEGER, name TEXT, start_msg TEXT, end_msg TEXT, tod REAL,
                                first_wave_after REAL, target_npc_spawner_id INTEGER, kill_npc_id INTEGER,
                                kill_npc_count INTEGER, force_end_time REAL, tod_day_interval INTEGER,
                                title_msg TEXT, milestone_id INTEGER);
                            CREATE TABLE tower_def_progs (
                                id INTEGER, tower_def_id INTEGER, msg TEXT, cond_to_next_time REAL,
                                cond_comp_by_and TEXT);
                            CREATE TABLE tower_def_prog_spawn_targets (
                                id INTEGER, tower_def_prog_id INTEGER, spawn_target_id INTEGER,
                                spawn_target_type TEXT, despawn_on_next_step TEXT);
                            CREATE TABLE tower_def_prog_kill_targets (
                                id INTEGER, tower_def_prog_id INTEGER, kill_target_id INTEGER,
                                kill_target_type TEXT, kill_count INTEGER);
                            """);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
