using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public class RiftPlacementTests
{
    [Test]
    public async Task RetailCrimson_AllWaveControllers_UseTheSitesAirborneLaunchHeight()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "Data");
        var manifest = JsonHelper.DeserializeObject<TowerDefenseManifest>(await File.ReadAllTextAsync(
            Path.Combine(data, "TowerDefense", "retail-rifts.json")));
        var placements = JsonHelper.DeserializeObject<List<NpcSpawner>>(await File.ReadAllTextAsync(
            Path.Combine(data, "Worlds", "main_world", "npc_spawns_tower_defense.json")))
            .ToDictionary(value => value.EventPlacementId);
        var checkedCount = 0;
        foreach (var site in manifest.Events.Where(evt => evt.Key.StartsWith("rift.crimson.", StringComparison.Ordinal))
                     .SelectMany(evt => evt.Sites))
        {
            var controllers = site.Bindings.Where(binding => binding.Key != "initial")
                .SelectMany(binding => binding.Value).Select(id => placements[id]).ToList();
            foreach (var controller in controllers)
            {
                await Assert.That(controller.EventSiteKey).IsEqualTo(site.Key);
                await Assert.That(controller.StartInactive).IsTrue();
                await Assert.That(controller.Position.Z > site.Anchor.Z + 50f).IsTrue();
                await Assert.That(controller.Position.Z).IsEqualTo(controllers[0].Position.Z);
                checkedCount++;
            }
        }
        await Assert.That(checkedCount).IsEqualTo(25);
    }

    [Test]
    [Arguments("grim-cinderstone", 109220u, 12718u, 12901u)]
    [Arguments("grim-cinderstone", 109221u, 12719u, 12902u)]
    [Arguments("grim-ynystere", 111327u, 12718u, 12901u)]
    [Arguments("grim-ynystere", 111326u, 12719u, 12902u)]
    public async Task RetailGrimghast_InfantryFormations_MeetBothFifteenKillObjectives(
        string site, uint spawner, uint firstNpc, uint secondNpc)
    {
        var placements = JsonHelper.DeserializeObject<List<NpcSpawner>>(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "npc_spawns_tower_defense.json")));
        var formation = placements.Where(p => p.EventSiteKey == site && p.NpcSpawnerIds.Contains(spawner)).ToList();
        await Assert.That(formation.Count(p => p.UnitId == firstNpc)).IsEqualTo(15);
        await Assert.That(formation.Count(p => p.UnitId == secondNpc)).IsEqualTo(15);
        await Assert.That(formation.All(p => p.StartInactive)).IsTrue();
    }
}
