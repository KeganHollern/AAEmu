using System.Numerics;

using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.AI.v2.Params.Almighty;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class FollowPathBehavior : BaseCombatBehavior
{
    private bool _enter;

    public override void Enter()
    {
        Ai.Owner.InterruptSkills();

        if (ShouldUseDragonFlightPatrol())
        {
            Ai.Owner.Buffs.RemoveBuff(DragonGroundBuffId);
            if (!Ai.Owner.Buffs.CheckBuff(DragonFlightBuffId))
                Ai.Owner.Buffs.AddBuff(DragonFlightBuffId, Ai.Owner);

            Ai.Owner.CanFly = true;
            Ai.Owner.CurrentGameStance = GameStanceType.Fly;
            Ai.PathHandler.AiPathSpeed = Ai.Owner.BaseMoveSpeed;
        }
        else
        {
            Ai.Owner.CurrentGameStance = GameStanceType.Relaxed;
        }

<<<<<<< Updated upstream
        Ai.Owner.IsInPatrol = true;
        Ai.Owner.Simulation.MoveToPathEnabled = true;
        Ai.Owner.Simulation.GoToPath(Ai.Owner, true);

=======
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Idle;
        Ai.Owner.BroadcastPacket(new SCUnitModelPostureChangedPacket(Ai.Owner, Ai.Owner.AnimActionId, false), false);

        // Path movement is driven solely by Ai.PathHandler in Tick(). The legacy Simulation route
        // fought the path handler for control of the same NPC.
        Ai.Owner.IsInPatrol = true;
>>>>>>> Stashed changes
        _enter = true;
    }

    public override void Tick(TimeSpan delta)
    {
        if (!_enter)
            return;

        if (!UpdateTarget())
            Ai.Owner.SetTarget(null);

        if (CheckAggression())
            return;

        if (CheckAlert())
            return;

        // An attacked patrol keeps its authored route data, but combat owns movement and skills.
        if (Ai.Owner.IsInBattle && !Ai.Owner.AggroTable.IsEmpty)
        {
            Ai.GoToCombat();
            return;
        }

<<<<<<< Updated upstream
        if (!Ai.PathHandler.RunCurrentPath(delta))
=======
        var hasPathMovementLeft = Ai.PathHandler.RunCurrentPath(delta);

        // A ReturnToCommandSet point can switch behavior from inside RunCurrentPath.
        if (!ReferenceEquals(Ai.GetCurrentBehavior(), this))
            return;

        if (!hasPathMovementLeft)
>>>>>>> Stashed changes
        {
            Ai.GoToIdle();
        }

        if (Ai.PathHandler.TargetPosition == Vector3.Zero &&
            Ai.PathHandler.AiPathPoints.Count <= 0 &&
            Ai.PathHandler.AiPathPointsRemaining.Count <= 0)
        {
            Ai.GoToIdle();
        }
    }

    private bool ShouldUseDragonFlightPatrol()
    {
        if (Ai.Owner.Template.AiParams is not AlmightyNpcAiParams aiParams)
            return false;

        var healthRatio = Ai.Owner.MaxHp > 0 ? (float)Ai.Owner.Hp / Ai.Owner.MaxHp * 100f : 0f;
        var phaseIndex = AlmightyAttackBehavior.SelectPhaseIndex(
            aiParams.AiSkillLists,
            healthRatio,
            0,
            -1,
            aiParams.AiPhaseChangeType == 1);

        return phaseIndex >= 0 && AlmightyAttackBehavior.IsDragonPathPhase(aiParams.AiSkillLists[phaseIndex]);
    }

    public override void Exit()
    {
<<<<<<< Updated upstream
=======
        Ai.Owner.IsInPatrol = false;
>>>>>>> Stashed changes
        _enter = false;
    }
}
