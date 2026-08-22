using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Params.Almighty;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Behaviors;

public class AlmightyAttackBehaviorPhaseTests
{
    [Test]
    [Arguments(100f, 0)]
    [Arguments(80f, 1)]
    [Arguments(65f, 2)]
    [Arguments(50f, 3)]
    [Arguments(30f, 4)]
    [Arguments(15f, 5)]
    [Arguments(0.1f, 5)]
    public async Task SelectPhaseIndex_UsesAuthoredDragonHealthBands(float healthRatio, int expectedPhase)
    {
        var selected = AlmightyAttackBehavior.SelectPhaseIndex(
            CreateDragonPhases(),
            healthRatio,
            0,
            -1,
            true);

        await Assert.That(selected).IsEqualTo(expectedPhase);
    }

    [Test]
    public async Task SelectPhaseIndex_SequentialEncounter_DoesNotRegressAfterHealing()
    {
        var selected = AlmightyAttackBehavior.SelectPhaseIndex(
            CreateDragonPhases(),
            70f,
            0,
            2,
            true);

        await Assert.That(selected).IsEqualTo(2);
    }

    [Test]
    public async Task SelectPhaseIndex_NonSequentialEncounter_CanReturnToMatchingHealthBand()
    {
        var selected = AlmightyAttackBehavior.SelectPhaseIndex(
            CreateDragonPhases(),
            70f,
            0,
            2,
            false);

        await Assert.That(selected).IsEqualTo(1);
    }

    [Test]
    [Arguments(9.9, -1)]
    [Arguments(10.0, 0)]
    [Arguments(20.0, 0)]
    [Arguments(20.1, -1)]
    public async Task SelectPhaseIndex_RespectsAuthoredTimeWindow(double elapsed, int expectedPhase)
    {
        var phases = new List<AiSkillList>
        {
            new()
            {
                HealthRangeMin = 0,
                HealthRangeMax = 0,
                TimeRangeStart = 10,
                TimeRangeEnd = 20
            }
        };

        var selected = AlmightyAttackBehavior.SelectPhaseIndex(phases, 100f, elapsed, -1, false);

        await Assert.That(selected).IsEqualTo(expectedPhase);
    }

    private static List<AiSkillList> CreateDragonPhases()
    {
        return
        [
            CreatePhase(80, 100, "phase_dragon_fly_path", 0),
            CreatePhase(65, 80, "phase_dragon_ground", 1),
            CreatePhase(50, 65, "phase_dragon_ground", 1),
            CreatePhase(30, 50, "phase_dragon_fly_hovering", 2),
            CreatePhase(15, 30, "phase_dragon_ground", 1),
            CreatePhase(0, 15, "phase_dragon_ground", 1)
        ];
    }

    private static AiSkillList CreatePhase(int minHealth, int maxHealth, string pipeName, uint phaseType)
    {
        return new AiSkillList
        {
            HealthRangeMin = minHealth,
            HealthRangeMax = maxHealth,
            PipeName = pipeName,
            PhaseType = phaseType
        };
    }
}
