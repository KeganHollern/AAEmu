using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// Covers the unpinned dump-point binding selection introduced for aaemu-cluster#92 (#94):
/// inactive staging rows (activation_state=f) must no longer double-spawn next to the live row.
/// </summary>
public class SpawnManagerTests
{
    [Test]
    public async Task SelectSpawnerTemplateIds_SingleTemplate_KeepsItEvenWhenInactive()
    {
        var templates = new List<NpcSpawnerTemplate>
        {
            CreateTemplate(13392, active: false)
        };

        var selected = SpawnManager.SelectSpawnerTemplateIds(templates);

        await Assert.That(selected).IsEquivalentTo(new List<uint> { 13392 });
    }

    [Test]
    public async Task SelectSpawnerTemplateIds_MultipleTemplates_BindsOnlyActiveOnes()
    {
        var templates = new List<NpcSpawnerTemplate>
        {
            CreateTemplate(13391, active: true),
            CreateTemplate(13392, active: false),
            CreateTemplate(13393, active: true)
        };

        var selected = SpawnManager.SelectSpawnerTemplateIds(templates);

        await Assert.That(selected).IsEquivalentTo(new List<uint> { 13391, 13393 });
    }

    [Test]
    public async Task SelectSpawnerTemplateIds_MultipleTemplatesNoneActive_FallsBackToAll()
    {
        var templates = new List<NpcSpawnerTemplate>
        {
            CreateTemplate(101, active: false),
            CreateTemplate(102, active: false)
        };

        var selected = SpawnManager.SelectSpawnerTemplateIds(templates);

        await Assert.That(selected).IsEquivalentTo(new List<uint> { 101, 102 });
    }

    [Test]
    public async Task SelectSpawnerTemplateIds_NullTemplatesAreIgnored()
    {
        var templates = new List<NpcSpawnerTemplate>
        {
            null,
            CreateTemplate(201, active: false),
            CreateTemplate(202, active: true)
        };

        var selected = SpawnManager.SelectSpawnerTemplateIds(templates);

        await Assert.That(selected).IsEquivalentTo(new List<uint> { 202 });
    }

    [Test]
    public async Task SelectSpawnerTemplateIds_NoResolvableTemplates_ReturnsEmpty()
    {
        var selected = SpawnManager.SelectSpawnerTemplateIds(new List<NpcSpawnerTemplate> { null, null });

        await Assert.That(selected).IsEmpty();
    }

    private static NpcSpawnerTemplate CreateTemplate(uint id, bool active)
    {
        return new NpcSpawnerTemplate
        {
            Id = id,
            ActivationState = active,
            Npcs = []
        };
    }
}
