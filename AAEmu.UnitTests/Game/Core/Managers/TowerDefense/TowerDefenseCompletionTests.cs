using AAEmu.Game.Core.Managers.TowerDefense;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.UnitTests.Game.Core.Managers.TowerDefense;

public class TowerDefenseCompletionTests
{
    [Test]
    public async Task CreateTerminalObjective_ExplicitManifestBoss_OverridesUnusableCompactTarget()
    {
        var manifest = new TowerDefenseEventManifest
        {
            CompletionTarget = new TowerDefenseCompletionTarget { NpcId = 8850, Count = 2 }
        };
        var definition = new TowerDef { KillNpcId = 1, KillNpcCount = 0 };
        var objective = TowerDefenseManager.CreateTerminalObjective(manifest, definition);

        await Assert.That(objective.TargetId).IsEqualTo(8850u);
        await Assert.That(objective.Required).IsEqualTo(2u);
        await Assert.That(objective.Increment().Current).IsEqualTo(1u);
        await Assert.That(objective.Increment().Increment().Current).IsEqualTo(2u);
        await Assert.That(definition.KillNpcCount).IsEqualTo(0u);
    }

    [Test]
    public async Task CreateTerminalObjective_NoOverride_PreservesCompactObjective()
    {
        var objective = TowerDefenseManager.CreateTerminalObjective(new TowerDefenseEventManifest(),
            new TowerDef { KillNpcId = 100, KillNpcCount = 3 });
        await Assert.That(objective).IsEqualTo(new TowerDefenseObjectiveProgress(100, 3, 0));
    }

    [Test]
    [Arguments(0u, 1u)]
    [Arguments(1u, 0u)]
    public async Task CreateTerminalObjective_InvalidOverride_RejectsEmptyCriterion(uint npcId, uint count)
    {
        var manifest = new TowerDefenseEventManifest
        {
            CompletionTarget = new TowerDefenseCompletionTarget { NpcId = npcId, Count = count }
        };
        await Assert.That(() => TowerDefenseManager.CreateTerminalObjective(manifest, new TowerDef()))
            .Throws<InvalidDataException>();
    }
}
