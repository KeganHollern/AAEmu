using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// Covers the unpinned dump-point binding selection for aaemu-cluster#92 (#94 + validation
/// round 2): inactive staging rows must not double-spawn next to the live row, and of several
/// free-running active rows exactly ONE is bound (they all place the same npc on the same dump
/// coordinates — Allistair x2, Blackbeard trash x3). Schedule/time-window rows are kept: they
/// only run inside their window.
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
    public async Task SelectSpawnerTemplateIds_MultipleFreeRunningActives_BindsExactlyOnePreferringAutocreated()
    {
        var templates = new List<NpcSpawnerTemplate>
        {
            CreateTemplate(13387, active: true, category: NpcSpawnerCategory.Normal),
            CreateTemplate(13382, active: true, category: NpcSpawnerCategory.Autocreated),
            CreateTemplate(13392, active: false)
        };

        var selected = SpawnManager.SelectSpawnerTemplateIds(templates);

        await Assert.That(selected).IsEquivalentTo(new List<uint> { 13382 });
    }

    [Test]
    public async Task SelectSpawnerTemplateIds_KeepsScheduleGatedRowsBesideTheFreeRunningOne()
    {
        var templates = new List<NpcSpawnerTemplate>
        {
            CreateTemplate(301, active: true, category: NpcSpawnerCategory.Autocreated),
            CreateTemplate(302, active: true),                       // schedule-bound (predicate below)
            CreateTemplate(303, active: true, start: 6f, end: 20f)   // time-window row
        };

        var selected = SpawnManager.SelectSpawnerTemplateIds(templates, id => id == 302);

        await Assert.That(selected).IsEquivalentTo(new List<uint> { 301, 302, 303 });
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

    private static NpcSpawnerTemplate CreateTemplate(uint id, bool active,
        NpcSpawnerCategory category = NpcSpawnerCategory.Normal, float start = 0f, float end = 0f)
    {
        return new NpcSpawnerTemplate
        {
            Id = id,
            ActivationState = active,
            NpcSpawnerCategoryId = category,
            StartTime = start,
            EndTime = end,
            Npcs = []
        };
    }
}
