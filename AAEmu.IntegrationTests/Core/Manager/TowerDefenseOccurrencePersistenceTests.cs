using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.TowerDefense;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[CollectionDefinition(nameof(TowerDefenseMySql), DisableParallelization = true)]
public sealed class TowerDefenseMySql : ICollectionFixture<GameMySqlFixture>;

[Collection(nameof(TowerDefenseMySql))]
[Trait("Category", "GameMySql")]
public sealed class TowerDefenseOccurrencePersistenceTests
{
    [Fact]
    public void StartManual_FailedFirstInsert_AllowsSameOccurrenceToRetryOnce()
    {
        var definitions = (IDictionary<uint, TowerDef>)TowerDefGameData.Instance.TowerDefs;
        const uint defId = 820288;
        var manifestPath = Path.GetTempFileName();
        definitions.Add(defId, new TowerDef { Id = defId, ForceEndTime = 3600, TimeOfDayDayInterval = 1 });
        try
        {
            File.WriteAllText(manifestPath, """
                {"schemaVersion":1,"events":[{"key":"test-rift","towerDefId":820288,
                "trigger":{"type":"Manual"},"sites":[{"key":"coast","spotId":1}]}]}
                """);
            var world = new WorldInstance(new WorldTemplate { Name = "main_world" }, 0, true, 1);
            world.SpawnManager = new SpawnManager(world);
            var worlds = new Mock<IWorldManager>();
            worlds.Setup(value => value.GetWorlds()).Returns([world]);
            worlds.Setup(value => value.GetZoneId(world.Template, 0, 0)).Returns(7u);
            var zones = new Mock<IZoneManager>();
            zones.Setup(value => value.GetZoneByKey(7)).Returns(new Zone { GroupId = 2 });
            var tasks = new Mock<ITaskManager>();
            var options = Options.Create(new AppConfiguration { TowerDefense = new() { ManifestPath = manifestPath } });
            TowerDefenseManager NewManager()
            {
                var manager = new TowerDefenseManager(Mock.Of<ITimeManager>(), worlds.Object, zones.Object,
                    tasks.Object, new FixedClock(), options);
                manager.Load();
                manager.Initialize();
                manager.OnWorldsInitialized();
                return manager;
            }

            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            using (var manager = NewManager())
            {
                command.CommandText = """
                    CREATE TRIGGER refuse_first_start BEFORE INSERT ON tower_def_occurrences
                    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='simulated insert failure'
                    """;
                command.ExecuteNonQuery();
                try
                {
                    Assert.False(manager.StartManual("test-rift", out var message));
                    Assert.Contains("save", message);
                    Assert.Empty(manager.GetActiveOccurrences());
                    Assert.Empty(tasks.Invocations);
                    command.CommandText = "SELECT COUNT(*) FROM tower_def_occurrences";
                    Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
                }
                finally
                {
                    command.CommandText = "DROP TRIGGER refuse_first_start";
                    command.ExecuteNonQuery();
                }

                Assert.True(manager.StartManual("test-rift", out var result), result);
                Assert.Single(manager.GetActiveOccurrences());
                Assert.True(manager.EndManual("test-rift", "test_complete", out result), result);
            }

            // The clock gives the new manager the same key after the first manager exits.
            using var restarted = NewManager();
            Assert.False(restarted.StartManual("test-rift", out var duplicate));
            Assert.Equal("Occurrence was already handled.", duplicate);
            Assert.Empty(restarted.GetActiveOccurrences());
            command.CommandText = "SELECT status, terminal_reason FROM tower_def_occurrences";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Ended", reader.GetString(0));
            Assert.Equal("test_complete", reader.GetString(1));
            Assert.False(reader.Read());
        }
        finally
        {
            definitions.Remove(defId);
            File.Delete(manifestPath);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddYears(60);
    }
}
