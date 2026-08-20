using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace AAEmu.UnitTests.Game.Core.Managers
{
    public class AccessLevelManagerTests
    {
        private AccessLevelManager _manager;
        private AppConfiguration _config;

        [Before(Test)]
        public void Setup()
        {
            _config = new AppConfiguration { AccessLevel = new Dictionary<string, int>() };
            _manager = new AccessLevelManager(Options.Create(_config));
        }

        [Test]
        public async Task GetLevel_WhenCommandNotExists_ShouldReturnDefaultLevel()
        {
            _manager.Load();
            var result = _manager.GetLevel("non_existent_command");
            await Assert.That(result).IsEqualTo(100);
        }

        [Test]
        public async Task GetLevel_WhenCommandExists_ShouldReturnCorrectLevel()
        {
            _config.AccessLevel["test_command"] = 5;

            _manager.Load();
            var result = _manager.GetLevel("test_command");
            await Assert.That(result).IsEqualTo(5);
        }

        [Test]
        public async Task GetLevel_WhenSubCommandExists_ShouldUseMostSpecificConfiguredPath()
        {
            _config.AccessLevel["npc"] = 100;
            _config.AccessLevel["npc surface"] = 0;

            _manager.Load();
            var result = _manager.GetLevel("npc", "surface", "target");

            await Assert.That(result).IsEqualTo(0);
        }

        [Test]
        public async Task GetLevel_WhenSubCommandAliasExists_ShouldUseAliasPath()
        {
            _config.AccessLevel["npc z"] = 0;

            _manager.Load();
            var result = _manager.GetLevel("npc", "z", "target");

            await Assert.That(result).IsEqualTo(0);
        }

        [Test]
        public async Task GetLevel_WhenSiblingSubCommandIsNotConfigured_ShouldReturnDefaultLevel()
        {
            _config.AccessLevel["npc surface"] = 0;

            _manager.Load();
            var result = _manager.GetLevel("npc", "spawn", "123");

            await Assert.That(result).IsEqualTo(100);
        }

        [Test]
        public async Task GetLevel_WhenOnlyParentExists_ShouldUseParentLevel()
        {
            _config.AccessLevel["world"] = 50;

            _manager.Load();
            var result = _manager.GetLevel("world", "set", "growthrate");

            await Assert.That(result).IsEqualTo(50);
        }

        [Test]
        public async Task GetLevel_NpcSurfaceProductionPolicy_AllowsDiagnosticAndDeniesMutatingSiblings()
        {
            _config.AccessLevel["npc surface"] = 0;
            _config.AccessLevel["npc z"] = 0;

            _manager.Load();

            await Assert.That(_manager.GetLevel("npc", "surface", "target")).IsEqualTo(0);
            await Assert.That(_manager.GetLevel("npc", "z", "target")).IsEqualTo(0);
            await Assert.That(_manager.GetLevel("npc")).IsEqualTo(100);
            await Assert.That(_manager.GetLevel("npc", "info")).IsEqualTo(100);
            await Assert.That(_manager.GetLevel("npc", "position")).IsEqualTo(100);
            await Assert.That(_manager.GetLevel("npc", "pos")).IsEqualTo(100);
            await Assert.That(_manager.GetLevel("npc", "save")).IsEqualTo(100);
            await Assert.That(_manager.GetLevel("npc", "spawn")).IsEqualTo(100);
            await Assert.That(_manager.GetLevel("npc", "remove")).IsEqualTo(100);
        }

        [Test]
        public async Task GetLevel_DoodadPlacementProductionPolicy_RequiresAdmin()
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "Configurations", "AccessLevels.json");
            var productionConfig = JsonConvert.DeserializeObject<AppConfiguration>(
                await File.ReadAllTextAsync(configPath));
            var manager = new AccessLevelManager(Options.Create(productionConfig));

            manager.Load();

            await Assert.That(manager.GetLevel("doodad", "edit", "nearest", "5541")).IsEqualTo(100);
            await Assert.That(manager.GetLevel("doodad", "place", "nearest", "5541")).IsEqualTo(100);
        }

        [Test]
        public async Task Load_ShouldLoadMultipleCommandsCorrectly()
        {
            _config.AccessLevel["cmd1"] = 1;
            _config.AccessLevel["cmd2"] = 2;
            _config.AccessLevel["cmd3"] = 3;

            _manager.Load();
            await Assert.That(_manager.GetLevel("cmd1")).IsEqualTo(1);
            await Assert.That(_manager.GetLevel("cmd2")).IsEqualTo(2);
            await Assert.That(_manager.GetLevel("cmd3")).IsEqualTo(3);
        }

        [Test]
        public async Task Load_WhenDuplicateCommands_ShouldOverwriteLevel()
        {
            _config.AccessLevel["duplicate"] = 5;
            _config.AccessLevel["duplicate"] = 10;

            _manager.Load();
            await Assert.That(_manager.GetLevel("duplicate")).IsEqualTo(10);
        }

        [Test]
        public async Task Load_WhenEmptyConfig_ShouldNotLoadCommands()
        {
            _manager.Load();
            await Assert.That(_manager.GetLevel("any_command")).IsEqualTo(100);
        }
    }
}
