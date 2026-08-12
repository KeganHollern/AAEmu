using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.SkillControllers;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Tasks.UnitMove;

namespace AAEmu.UnitTests.Game.Models.Game.Units.Route;

public class SimulationTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 1)]
    public async Task Move_Execute_RespectsPathMovementCancellation(bool enabled, int expectedMovementReads)
    {
        var npc = new ProbeNpc
        {
            Hp = 1,
            ActiveSkillController = new RunningSkillController()
        };
        var simulation = new Simulation(npc)
        {
            MoveToPathEnabled = enabled
        };
        var move = new Move(simulation, npc, 10f, 0f, 0f);

        move.Execute();

        await Assert.That(npc.BaseMoveSpeedReadCount).IsEqualTo(expectedMovementReads);
        await Assert.That(npc.MoveSpeedMultiplierReadCount).IsEqualTo(expectedMovementReads);
    }

    private sealed class ProbeNpc : Npc
    {
        public int BaseMoveSpeedReadCount { get; private set; }
        public int MoveSpeedMultiplierReadCount { get; private set; }

        public override float BaseMoveSpeed
        {
            get
            {
                BaseMoveSpeedReadCount++;
                return 1f;
            }
        }

        public override float MoveSpeedMul
        {
            get
            {
                MoveSpeedMultiplierReadCount++;
                return 1f;
            }
        }
    }

    private sealed class RunningSkillController : SkillController
    {
        public RunningSkillController()
        {
            State = SkillController.SCState.Running;
        }
    }
}
