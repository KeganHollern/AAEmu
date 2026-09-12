using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// Guards the shipped Data/Worlds/*/quest_spheres.json supplements:
/// it must stay parseable by the same deserializer SphereQuestManager uses,
/// and every entry must describe a usable trigger volume. Quests listed here
/// have no client-side quest_sign_sphere geometry, so a broken supplement
/// silently makes them impossible to complete again (aaemu-cluster#78).
/// </summary>
public class SphereQuestSupplementTests
{
    private static string WorldPath(string world) =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", world);

    private static string SupplementPath(string world) =>
        Path.Combine(WorldPath(world), "quest_spheres.json");

    [Test]
    [Arguments("main_world")]
    [Arguments("instance_training_camp")]
    public async Task ShippedSupplementParsesWithProductionDeserializer(string world)
    {
        var contents = await File.ReadAllTextAsync(SupplementPath(world));

        var ok = JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out var exception);

        await Assert.That(ok).IsTrue();
        await Assert.That(exception).IsNull();
        await Assert.That(supplements).IsNotNull();
        await Assert.That(supplements.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    [Arguments("main_world")]
    [Arguments("instance_training_camp")]
    public async Task EveryEntryDescribesAUsableTriggerVolume(string world)
    {
        var contents = await File.ReadAllTextAsync(SupplementPath(world));
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);

        foreach (var entry in supplements)
        {
            await Assert.That(entry.QuestId).IsGreaterThan(0u);
            await Assert.That(entry.ComponentId).IsGreaterThan(0u);
            await Assert.That(float.IsFinite(entry.X) && float.IsFinite(entry.Y) && float.IsFinite(entry.Z)).IsTrue();
            await Assert.That(float.IsFinite(entry.Radius)).IsTrue();
            await Assert.That(entry.Radius).IsGreaterThan(0f);
            // World origin means a forgotten position; no legitimate trigger sits there.
            await Assert.That(entry.X != 0f || entry.Y != 0f).IsTrue();
        }
    }

    [Test]
    public async Task ContainsBorrowedBraverySphereAtVorden()
    {
        var contents = await File.ReadAllTextAsync(SupplementPath("main_world"));
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);

        var entry = supplements.SingleOrDefault(s => s.QuestId == 1650);

        await Assert.That(entry).IsNotNull();
        // Component 7764 is quest 1650's QuestActObjSphere component; the sphere is
        // centered on Vorden's (npc 3535) spawn outside Blackreath Keep.
        await Assert.That(entry.ComponentId).IsEqualTo(7764u);
        await Assert.That(entry.Radius).IsEqualTo(10f);
    }

    [Test]
    [Arguments(5716u, 24586u, "npc_spawns.json", 13734u)]
    [Arguments(6213u, 26636u, "doodad_spawns.json", 1886u)]
    [Arguments(6216u, 26646u, "doodad_spawns.json", 7900u)]
    public async Task ObjectiveSphereUsesItsDeployedDestination(uint questId, uint componentId, string spawnFile, uint unitId)
    {
        var contents = await File.ReadAllTextAsync(SupplementPath("main_world"));
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);
        var spawnContents = await File.ReadAllTextAsync(Path.Combine(WorldPath("main_world"), spawnFile));
        JsonHelper.TryDeserializeObject(spawnContents, out List<DestinationSpawn> spawns, out _);

        var sphere = supplements.Single(s => s.QuestId == questId && s.ComponentId == componentId);
        var destination = spawns.Single(s => s.UnitId == unitId).Position;

        await Assert.That(sphere.X).IsEqualTo(destination.X);
        await Assert.That(sphere.Y).IsEqualTo(destination.Y);
        await Assert.That(sphere.Z).IsEqualTo(destination.Z);
    }

    [Test]
    public async Task BurntCastleJailbreakStarter_UsesExactMissionRadiusAndContainsCurrentCaptive()
    {
        var contents = await File.ReadAllTextAsync(SupplementPath("main_world"));
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);
        var entry = supplements.Single(s => s.QuestId == 578 && s.ComponentId == 2319);
        var npcContents = await File.ReadAllTextAsync(Path.Combine(WorldPath("main_world"), "npc_spawns.json"));
        JsonHelper.TryDeserializeObject(npcContents, out List<DestinationSpawn> spawns, out _);
        var captive = spawns.Single(s => s.UnitId == 2445).Position;
        var sphere = new SphereQuest { Xyz = new Vector3(entry.X, entry.Y, entry.Z), Radius = entry.Radius };

        await Assert.That(sphere.Xyz).IsEqualTo(new Vector3(15496.9167f, 12534.2473f, 148.44249f));
        await Assert.That(entry.ZoneId).IsEqualTo(257u);
        await Assert.That(sphere.Radius).IsEqualTo(5f);
        await Assert.That(sphere.Contains(new Vector3(captive.X, captive.Y, captive.Z))).IsTrue();
        await Assert.That(sphere.Contains(sphere.Xyz + new Vector3(0, 0, 5f))).IsTrue();
        await Assert.That(sphere.Contains(sphere.Xyz + new Vector3(0, 0, 5.01f))).IsFalse();
    }

    [Test]
    public async Task DrillCampSphereCoversBothTeamEntryPointsAndReportNpcs()
    {
        var contents = await File.ReadAllTextAsync(SupplementPath("instance_training_camp"));
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);
        var battlefieldContents = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Data", "battlefields.json"));
        JsonHelper.TryDeserializeObject(battlefieldContents, out List<BattlefieldSpawns> battlefields, out _);
        var npcContents = await File.ReadAllTextAsync(Path.Combine(WorldPath("instance_training_camp"), "npc_spawns.json"));
        JsonHelper.TryDeserializeObject(npcContents, out List<DestinationSpawn> spawns, out _);
        var battlefield = battlefields.Single(b => b.BattlefieldId == 7);
        var destinations = new[] { battlefield.Corps1Spawn, battlefield.Corps2Spawn }
            .Concat(spawns.Where(s => s.UnitId == 14168).Select(s => s.Position));
        var spheres = supplements.Where(s => s.QuestId == 5979 && s.ComponentId == 25757).ToArray();

        await Assert.That(spheres.Length).IsEqualTo(2);
        foreach (var destination in destinations)
        {
            var position = new Vector3(destination.X, destination.Y, destination.Z);
            await Assert.That(spheres.Any(s => Vector3.Distance(new Vector3(s.X, s.Y, s.Z), position) <= s.Radius)).IsTrue();
        }
    }

    private sealed class DestinationSpawn
    {
        public uint UnitId { get; set; }
        public Point Position { get; set; }
    }
}
