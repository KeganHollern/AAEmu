using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// Guards the shipped Data/Worlds/main_world/quest_spheres.json supplement:
/// it must stay parseable by the same deserializer SphereQuestManager uses,
/// and every entry must describe a usable trigger volume. Quests listed here
/// have no client-side quest_sign_sphere geometry, so a broken supplement
/// silently makes them impossible to complete again (aaemu-cluster#78).
/// </summary>
public class SphereQuestSupplementTests
{
    private static string SupplementPath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "quest_spheres.json");

    [Test]
    public async Task ShippedSupplementParsesWithProductionDeserializer()
    {
        var contents = await File.ReadAllTextAsync(SupplementPath);

        var ok = JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out var exception);

        await Assert.That(ok).IsTrue();
        await Assert.That(exception).IsNull();
        await Assert.That(supplements).IsNotNull();
        await Assert.That(supplements.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task EveryEntryDescribesAUsableTriggerVolume()
    {
        var contents = await File.ReadAllTextAsync(SupplementPath);
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);

        foreach (var entry in supplements)
        {
            await Assert.That(entry.QuestId).IsGreaterThan(0u);
            await Assert.That(entry.ComponentId).IsGreaterThan(0u);
            await Assert.That(entry.Radius).IsGreaterThan(0f);
            // World origin means a forgotten position; no legitimate trigger sits there.
            await Assert.That(entry.X != 0f || entry.Y != 0f).IsTrue();
        }
    }

    [Test]
    public async Task ContainsBorrowedBraverySphereAtVorden()
    {
        var contents = await File.ReadAllTextAsync(SupplementPath);
        JsonHelper.TryDeserializeObject(contents, out List<QuestSphereSupplement> supplements, out _);

        var entry = supplements.SingleOrDefault(s => s.QuestId == 1650);

        await Assert.That(entry).IsNotNull();
        // Component 7764 is quest 1650's QuestActObjSphere component; the sphere is
        // centered on Vorden's (npc 3535) spawn outside Blackreath Keep.
        await Assert.That(entry.ComponentId).IsEqualTo(7764u);
        await Assert.That(entry.Radius).IsEqualTo(30f);
    }
}
