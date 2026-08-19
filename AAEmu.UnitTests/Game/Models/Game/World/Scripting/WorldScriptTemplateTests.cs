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
            if (rule.OnNpcKilled != null) conditions++;

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
    public async Task RetailKillChainsAreWired()
    {
        var rules = LoadRules();

        // Vera's death stages boss Nerta and the hideout Allistair (retail NpcSpawnerSpawnEffect
        // rows point at legacy spawner ids absent from this compact, so the script owns the chain).
        var vera = rules.Single(r => r.OnNpcKilled != null && r.OnNpcKilled.NpcTemplateIds.Contains(12150u));
        var staged = vera.Actions.SelectMany(a => a.ActivateNpcSpawners ?? []).ToList();
        await Assert.That(staged.Contains(13437u)).IsTrue();
        await Assert.That(staged.Contains(13389u)).IsTrue();

        // Okape's death (either variant) spawns the exit portal 5919 and his log 6197 — retail
        // did this from on-death skill 19755 "Okape's Destiny", whose effect rows never shipped.
        var okape = rules.Single(r => r.OnNpcKilled != null && r.OnNpcKilled.NpcTemplateIds.Contains(12188u));
        await Assert.That(okape.OnNpcKilled.NpcTemplateIds.Contains(11364u)).IsTrue();
        var spawned = okape.Actions.SelectMany(a => a.SpawnDoodads ?? []).Select(s => s.TemplateId).ToList();
        await Assert.That(spawned.Contains(5919u)).IsTrue();
        await Assert.That(spawned.Contains(6197u)).IsTrue();
    }

    [Test]
    public async Task KegWallRulesAreSpatiallyDisambiguated()
    {
        var rules = LoadRules();

        // Both walls use Rock 5280 and both keg clusters use Powder Keg 5282: the trigger and the
        // phase-change action MUST carry Near filters or one keg would open every wall.
        var kegRules = rules.Where(r => r.OnDoodadPhase is { DoodadTemplateId: 5282u }).ToList();
        await Assert.That(kegRules.Count).IsEqualTo(2);
        foreach (var rule in kegRules)
        {
            await Assert.That(rule.OnDoodadPhase.Near).IsNotNull();
            await Assert.That(rule.OnDoodadPhase.Near.Radius).IsGreaterThan(0);
            var change = rule.Actions.Single(a => a.ChangeDoodadPhase != null).ChangeDoodadPhase;
            await Assert.That(change.DoodadTemplateId).IsEqualTo(5280u);
            await Assert.That(change.Near).IsNotNull();
            await Assert.That(change.Near.Radius).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task RetailBeatsUseClientLocalizedBubbles()
    {
        var rules = LoadRules();

        // Every scripted line references a retail bubble_effects row so clients render their own
        // locale; authored Text is reserved for beats with no retail line (none currently).
        var says = rules.SelectMany(r => r.Actions).Where(a => a.Say != null).Select(a => a.Say).ToList();
        await Assert.That(says.Count).IsGreaterThanOrEqualTo(20);
        foreach (var say in says)
            await Assert.That(say.BubbleId).IsGreaterThan(0u);
    }

    [Test]
    public async Task EntranceUsesRetailCommandSetNotHandTimedBubbles()
    {
        var rules = LoadRules();

        // Retail's mine-mouth sequence is ai_command_sets 185 (칼바람폐광_알리스테어0): three lines on
        // 1s beats, FollowPath aipath_alistair0_0 down the shaft ahead of the party, then a
        // self-despawn. Driving the set keeps retail's ordering, spacing and movement; the previous
        // hand-timed Say chain had invented delays and no movement at all.
        var entrance = rules.Single(r => r.Actions.Any(a => a.RunCommandSet is { CommandSetId: 185u }));
        var run = entrance.Actions.Single(a => a.RunCommandSet != null).RunCommandSet;

        await Assert.That(run.NpcTemplateId).IsEqualTo(12108u);
        await Assert.That(entrance.OnPlayerEnterArea).IsNotNull();
        // The sequence owns the whole beat, so no hand-timed bubbles may remain on this rule.
        await Assert.That(entrance.Actions.Any(a => a.Say != null)).IsFalse();
        // Live run: he spawned in the same second the rule fired, so the client had not rendered
        // him when the first line played. The start must lag the spawn.
        await Assert.That(run.DelaySeconds).IsGreaterThanOrEqualTo(3f);
        // Live run: a stalled walk never reached the set's self-despawn and left a permanent ghost
        // at the ledge, which also blocks the hand-off illusion. Keep a backstop despawn.
        var backstop = entrance.Actions.Single(a => a.CastSkill != null).CastSkill;
        await Assert.That(backstop.NpcTemplateId).IsEqualTo(12108u);
        await Assert.That(backstop.SkillId).IsEqualTo(19430u);
        await Assert.That(backstop.DelaySeconds).IsGreaterThan(run.DelaySeconds);
    }

    [Test]
    public async Task PoolGreeterSpawnsAtZoneIn()
    {
        var contents = await File.ReadAllTextAsync(Path.Combine(WorldDir, "npc_spawns.json"));
        JsonHelper.TryDeserializeObject(contents, out List<PinProbe> spawns, out _);

        // Allistair 12109 stands at the pit pool from zone-in and greets the party after the drop.
        // Both of his compact spawner rows are activation_state='t', so pinning him to the staging
        // row left him dormant: the live run logged "npc 12109 not alive" and the whole pool beat,
        // plus his presence by the water, silently never happened. He must stay unpinned so the
        // selector binds his active 1:1 row, exactly like 12108 (which spawned correctly).
        var greeter = spawns.Single(s => s.UnitId == 12109);
        await Assert.That(greeter.NpcSpawnerIds).IsNull();
        await Assert.That(greeter.StartInactive).IsFalse();
    }

    [Test]
    public async Task MultiPlacementSpeakersAreDisambiguated()
    {
        var rules = LoadRules();
        var spawnContents = await File.ReadAllTextAsync(Path.Combine(WorldDir, "npc_spawns.json"));
        JsonHelper.TryDeserializeObject(spawnContents, out List<PinProbe> spawns, out _);

        // SayNow resolves a template with no Near filter through GetNpcByTemplateId, whose
        // ConcurrentDictionary order is arbitrary. Any speaker with several placements (the four
        // Sharpwind researchers) must therefore carry a Near filter or the bubble can appear over
        // an NPC far from the player who tripped the trigger.
        foreach (var say in rules.SelectMany(r => r.Actions).Where(a => a.Say != null).Select(a => a.Say))
        {
            var placements = spawns.Count(s => s.UnitId == say.NpcTemplateId);
            if (placements > 1)
            {
                await Assert.That(say.Near).IsNotNull();
                await Assert.That(say.Near.Radius).IsGreaterThan(0);
            }
        }
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
        // Allistair's staged segment clones pinned to their retail inactive spawners.
        await Assert.That(spawns.Single(s => s.UnitId == 12110).NpcSpawnerIds is [13389u]).IsTrue();
        await Assert.That(spawns.Single(s => s.UnitId == 12111).NpcSpawnerIds is [13390u]).IsTrue();
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
        public bool StartInactive { get; set; }
    }
}
