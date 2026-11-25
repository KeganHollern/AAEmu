using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.TowerDefense;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public class TowerDefenseObjectivePersistenceTests
{
    [Fact]
    public void DoodadObjectives_AreRejectedOnLoadStartAndRecovery()
    {
        var data = LoadDefinitions();
        Assert.True(data.Get(3).IsValid); // Crimson NPC spawner and NPC kill.
        Assert.True(data.Get(20).IsValid); // The emissary event has a terminal NPC objective.
        Assert.False(data.Get(4).IsValid);
        Assert.False(data.Get(26).IsValid);
        var errors = data.ValidationIssues.Where(issue => issue.Severity == TowerDefValidationSeverity.Error)
            .OrderBy(issue => issue.ChildId).ToArray();
        Assert.Equal(new uint[] { 6, 68, 70 }, errors.Select(issue => issue.ChildId));
        Assert.All(errors, issue => Assert.Contains("unsupported type 'DoodadAlmighty'", issue.Message));

        var manifests = new uint[] { 4, 26, 20 }.Select(id => new TowerDefenseEventManifest
        {
            TowerDefId = id, Key = $"fixture.{id}", Sites = [new() { Key = "fixture", SpotId = 1 }]
        }).ToList();
        var file = Path.GetTempFileName();
        var singleton = typeof(Singleton<TowerDefGameData>).GetField("s_instance",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        try
        {
            File.WriteAllText(file, JsonConvert.SerializeObject(new TowerDefenseManifest { SchemaVersion = 1, Events = manifests }));
            singleton.SetValue(null, data);
            var config = Options.Create(new AppConfiguration { TowerDefense = new() { Enabled = true, ManifestPath = file } });
            var world = new WorldInstance(new WorldTemplate { Name = "main_world" }, 0, true, 1);
            world.SpawnManager = new SpawnManager(world);
            var worlds = new Mock<IWorldManager>();
            worlds.Setup(manager => manager.GetWorlds()).Returns([world]);
            worlds.Setup(manager => manager.GetZoneId(world.Template, 0, 0)).Returns(42u);
            var zones = new Mock<IZoneManager>();
            zones.Setup(manager => manager.GetZoneByKey(42)).Returns(new Zone { GroupId = 20 });
            TowerDefenseManager NewManager() => new(Mock.Of<ITimeManager>(), worlds.Object, zones.Object,
                Mock.Of<ITaskManager>(), TimeProvider.System, config);

            string validKey;
            using (var manager = NewManager())
            {
                manager.Load();
                manager.Initialize();
                manager.OnWorldsInitialized();
                foreach (var id in new[] { 4, 26 })
                {
                    Assert.False(manager.StartManual($"fixture.{id}", out var reason));
                    Assert.Contains($"tower_def {id} is missing or invalid", reason);
                }
                Assert.Empty(manager.GetActiveOccurrences());
                Assert.True(manager.StartManual("fixture.20", out var result), result);
                var active = Assert.Single(manager.GetActiveOccurrences());
                Assert.Equal(TowerDefenseOccurrenceStatus.StepActive, active.Status);
                Assert.Equal(new TowerDefenseObjectiveProgress(13647, 1, 0), active.TerminalObjective);
                validKey = active.OccurrenceKey;
            }

            // These rows represent a process that saved its progress before validation changed.
            var repository = new TowerDefenseOccurrenceRepository();
            var now = DateTimeOffset.UtcNow;
            foreach (var manifest in manifests.Where(entry => entry.TowerDefId != 20))
                repository.Save(new TowerDefenseOccurrence
                {
                    OccurrenceKey = manifest.Key, Manifest = manifest, Site = manifest.Sites.Single(),
                    World = world, Definition = data.Get(manifest.TowerDefId), ScheduledAtUtc = now,
                    StartedAtUtc = now, HardDeadlineUtc = now.AddMinutes(10),
                    Status = TowerDefenseOccurrenceStatus.StepActive, CurrentStepOrdinal = 0, DefinitionHash = "prior"
                });

            using var recovered = NewManager();
            recovered.Load();
            recovered.Initialize();
            recovered.OnWorldsInitialized();
            var resumed = Assert.Single(recovered.GetActiveOccurrences());
            Assert.Equal(validKey, resumed.OccurrenceKey);
            Assert.Equal(new TowerDefenseObjectiveProgress(13647, 1, 0), resumed.TerminalObjective);
            using var db = MySQL.CreateConnection();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM tower_def_occurrences
                 WHERE occurrence_key IN ('fixture.4', 'fixture.26') AND status='Failed'
                   AND terminal_reason='definition_or_site_missing_after_restart'
                """;
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            singleton.SetValue(null, previous);
            File.Delete(file);
            using var db = MySQL.CreateConnection();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM tower_def_occurrences WHERE event_key IN ('fixture.4','fixture.20','fixture.26')";
            command.ExecuteNonQuery();
        }
    }

    private static TowerDefGameData LoadDefinitions()
    {
        // Project the r208022 compact rows needed for these four definitions.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE tower_defs(id, name, start_msg, end_msg, tod, first_wave_after, target_npc_spawner_id,
                kill_npc_id, kill_npc_count, force_end_time, tod_day_interval, title_msg, milestone_id);
            INSERT INTO tower_defs (id,tod,first_wave_after,force_end_time,tod_day_interval) VALUES
                (3,12,0,3600,1),(4,0,0,3600,1),(26,0,0,3600,1);
            INSERT INTO tower_defs (id,tod,first_wave_after,force_end_time,tod_day_interval,target_npc_spawner_id,kill_npc_id,kill_npc_count)
                VALUES (20,0,0,3600,1,15227,13647,1);
            CREATE TABLE tower_def_progs(id,tower_def_id,msg,cond_to_next_time,cond_comp_by_and);
            INSERT INTO tower_def_progs VALUES
                (2,3,'',0,'t'),(9,4,'',0,'t'),(106,20,'',3600,'t'),(144,26,'',20,'t'),(146,26,'',30,'t');
            CREATE TABLE tower_def_prog_spawn_targets(id,tower_def_prog_id,spawn_target_id,spawn_target_type,despawn_on_next_step);
            INSERT INTO tower_def_prog_spawn_targets VALUES
                (1,2,9848,'NpcSpawner','f'),(68,144,7297,'DoodadAlmighty','t'),(70,146,7297,'DoodadAlmighty','f');
            CREATE TABLE tower_def_prog_kill_targets(id,tower_def_prog_id,kill_target_id,kill_target_type,kill_count);
            INSERT INTO tower_def_prog_kill_targets VALUES (1,2,8834,'Npc',23),(6,9,3499,'DoodadAlmighty',1);
            """;
        command.ExecuteNonQuery();
        var data = new TowerDefGameData();
        data.Load(db);
        data.PostLoad();
        return data;
    }
}
