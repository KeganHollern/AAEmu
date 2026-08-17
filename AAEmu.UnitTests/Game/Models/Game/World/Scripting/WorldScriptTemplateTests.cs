using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.World.Scripting;

namespace AAEmu.UnitTests.Game.Models.Game.World.Scripting;

/// <summary>
/// Guards the Sharpwind Mines dungeon script and spawn-pin data
/// (aaemu-cluster#92): the script file must parse with the production
/// deserializer, every rule must have exactly one condition and at least one
/// action, and the staged NPC placements must stay pinned to their inactive
/// event spawners (unpinned they would spawn at dungeon start again).
/// </summary>
public class WorldScriptTemplateTests
{
    private static string WorldDir =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "instance_cuttingwind_deadmine");

    private static List<WorldScriptRule> LoadRules()
    {
        var contents = File.ReadAllText(Path.Combine(WorldDir, "dungeon_scripts.json"));
        JsonHelper.TryDeserializeObject(contents, out List<WorldScriptRule> rules, out _);
        return rules;
    }

    [Test]
    public async Task SharpwindScriptParsesWithProductionDeserializer()
    {
        var contents = await File.ReadAllTextAsync(Path.Combine(WorldDir, "dungeon_scripts.json"));

        var ok = JsonHelper.TryDeserializeObject(contents, out List<WorldScriptRule> rules, out var exception);

        await Assert.That(ok).IsTrue();
        await Assert.That(exception).IsNull();
        await Assert.That(rules.Count).IsGreaterThanOrEqualTo(5);
    }

    [Test]
    public async Task EveryRuleHasExactlyOneConditionAndActions()
    {
        foreach (var rule in LoadRules())
        {
            var conditions = 0;
            if (rule.OnDoodadPhase != null) conditions++;
            if (rule.OnAllDoodadsPhase != null) conditions++;
            if (rule.OnPlayerEnterArea != null) conditions++;

            await Assert.That(conditions).IsEqualTo(1);
            await Assert.That(rule.Actions).IsNotNull();
            await Assert.That(rule.Actions.Count).IsGreaterThanOrEqualTo(1);
            await Assert.That(string.IsNullOrWhiteSpace(rule.Name)).IsFalse();
        }
    }

    [Test]
    public async Task BridgeCollapseActivatesSlimePackAndCinematicNerta()
    {
        var rules = LoadRules();
        var bridge = rules.Single(r => r.OnDoodadPhase is { DoodadTemplateId: 5058u });

        await Assert.That(bridge.OnDoodadPhase.FuncGroupId).IsEqualTo(13148u);
        var activated = bridge.Actions.SelectMany(a => a.ActivateNpcSpawners ?? []).ToList();
        await Assert.That(activated.Contains(13392u)).IsTrue();
        await Assert.That(activated.Contains(13436u)).IsTrue();
    }

    [Test]
    public async Task GeyserCompletionRaisesLakeAndRaisedWaterCleansUp()
    {
        var rules = LoadRules();

        var geysers = rules.Single(r => r.OnAllDoodadsPhase is { DoodadTemplateId: 5064u });
        await Assert.That(geysers.Actions.Any(a => a.ChangeDoodadPhase is { DoodadTemplateId: 5063u, FuncGroupId: 14184u })).IsTrue();

        var raised = rules.Single(r => r.OnDoodadPhase is { DoodadTemplateId: 5063u, FuncGroupId: 13159u });
        var despawned = raised.Actions.SelectMany(a => a.DespawnNpcSpawners ?? []).ToList();
        await Assert.That(despawned.Contains(13392u)).IsTrue();
    }

    [Test]
    public async Task StagedNpcPlacementsStayPinnedToEventSpawners()
    {
        var contents = await File.ReadAllTextAsync(Path.Combine(WorldDir, "npc_spawns.json"));
        JsonHelper.TryDeserializeObject(contents, out List<PinProbe> spawns, out var exception);

        await Assert.That(exception).IsNull();
        // Slime pack: every placement pinned to the inactive event spawner 13392.
        var slimes = spawns.Where(s => s.UnitId == 11361).ToList();
        await Assert.That(slimes.Count).IsEqualTo(16);
        await Assert.That(slimes.All(s => s.NpcSpawnerIds is [13392u])).IsTrue();
        // Exactly one boss Nerta point, pinned to the inactive boss spawner.
        var boss = spawns.Where(s => s.UnitId == 11362).ToList();
        await Assert.That(boss.Count).IsEqualTo(1);
        await Assert.That(boss[0].NpcSpawnerIds is [13437u]).IsTrue();
        // Cinematic Nerta and Okape pinned.
        await Assert.That(spawns.Single(s => s.UnitId == 12146).NpcSpawnerIds is [13436u]).IsTrue();
        await Assert.That(spawns.Single(s => s.UnitId == 12188).NpcSpawnerIds is [13452u]).IsTrue();
    }

    [Test]
    public async Task LakeDoodadIsSpawnedAtThePool()
    {
        var contents = await File.ReadAllTextAsync(Path.Combine(WorldDir, "doodad_spawns.json"));
        JsonHelper.TryDeserializeObject(contents, out List<PinProbe> spawns, out _);

        var lake = spawns.SingleOrDefault(s => s.UnitId == 5063);
        await Assert.That(lake).IsNotNull();
    }

    /// <summary>Minimal shape probe for spawn-dump entries.</summary>
    private class PinProbe
    {
        public uint UnitId { get; set; }
        public List<uint> NpcSpawnerIds { get; set; }
    }
}
