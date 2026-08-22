using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Params.Almighty;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class AlmightyAttackBehavior : BaseCombatBehavior
{
    private AlmightyNpcAiParams _aiParams;
    private AiSkillList _activePhase;
    private int _activePhaseIndex = -1;
    private bool _queuePhaseEntrySkills;
    private bool _enter;

    public override void Enter()
    {
        Ai.Owner.InterruptSkills();
        _skillQueue = new Queue<AiSkill>();
        Ai.Owner.CurrentGameStance = GameStanceType.Combat;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Combat;
        Ai.Owner.BroadcastPacket(new SCUnitModelPostureChangedPacket(Ai.Owner, Ai.Owner.AnimActionId, false), false);

        _combatStartTime = DateTime.UtcNow;

        if (Ai.Owner is { IsInBattle: false } npc)
        {
            npc.Events.OnCombatStarted(this, new OnCombatStartedArgs { Owner = npc, Target = npc });
        }
        Ai.Param = Ai.Owner.Template.AiParams;
        _activePhase = null;
        _activePhaseIndex = -1;
        _queuePhaseEntrySkills = false;
        ResetPipeNameState();
        _enter = true;
    }

    public override void Tick(TimeSpan delta)
    {
        if (!_enter)
            return; // not initialized yet Enter()

        if (Ai.Param is not AlmightyNpcAiParams aiParams)
            return;

        _aiParams = aiParams;

        if (!UpdateTarget() || ShouldReturn)
        {
            Ai.OnNoAggroTarget();
            return;
        }

        if (!UpdateActivePhase())
            return;

        if (IsDragonPathPhase(_activePhase))
        {
            Ai.PathHandler.RunCurrentPath(delta);
        }
        else if (!IsDragonHoverPhase(_activePhase) && CanStrafe && !IsUsingSkill)
        {
            MoveInRange(Ai.Owner.CurrentTarget, delta);
        }

        if (!CanUseSkill)
            return;

        _strafeDuringDelay = false;

        #region Pick a skill

        var delay = 150;
        // Will delay for 150 Milliseconds to eliminate the hanging of the skill
        if (!Ai.Owner.CheckInterval(delay))
        {
            Logger.Trace($"Skill: CooldownTime [{delay}]!");
        }
        else
        {
            if (_skillQueue.Count == 0)
            {
                var queued = false;
                if (IsDragonPathPhase(_activePhase))
                {
                    var pathSkillList = SelectAvailableAiSkillList(_aiParams.AiPathSkillLists);
                    queued = QueueAiSkillList(pathSkillList, null, false);
                }

                queued |= QueueAiSkillList(_activePhase, _aiParams, _queuePhaseEntrySkills);
                _queuePhaseEntrySkills = false;

                if (!queued && !QueueBaseSkill())
                    return;
            }

            var selectedSkill = _skillQueue.Dequeue();
            if (selectedSkill == null)
                return;
            var skillTemplate = SkillManager.Instance.GetSkillTemplate(selectedSkill.SkillId);
            if (skillTemplate == null)
                return;

            UseSkill(new Skill(skillTemplate), Ai.Owner.CurrentTarget, selectedSkill.Delay);

            _strafeDuringDelay = selectedSkill.Strafe;
        }

        #endregion
    }

    private bool UpdateActivePhase()
    {
        var healthRatio = Ai.Owner.MaxHp > 0 ? (float)Ai.Owner.Hp / Ai.Owner.MaxHp * 100f : 0f;
        var timeElapsed = (DateTime.UtcNow - _combatStartTime).TotalSeconds;
        var nextPhaseIndex = SelectPhaseIndex(
            _aiParams.AiSkillLists,
            healthRatio,
            timeElapsed,
            _activePhaseIndex,
            _aiParams.AiPhaseChangeType == 1);

        if (nextPhaseIndex < 0)
            return false;

        if (nextPhaseIndex == _activePhaseIndex)
            return true;

        if (_activePhase != null)
            Ai.Owner.InterruptSkills();

        _activePhaseIndex = nextPhaseIndex;
        _activePhase = _aiParams.AiSkillLists[nextPhaseIndex];
        _skillQueue.Clear();
        _queuePhaseEntrySkills = true;
        _pipeName = _activePhase.PipeName;
        _phaseType = _activePhase.PhaseType;
        _aiParams.RestorationOnReturn = _activePhase.Restoration;
        _aiParams.GoReturnState = _activePhase.GoReturn;

        EnterDragonPhase(_activePhase);
        Logger.Info($"Almighty phase changed: Ai.Owner={Ai.Owner.ObjId}:{Ai.Owner.TemplateId}, phase={_activePhaseIndex}, pipe={_pipeName}, health={healthRatio:F1}");
        return true;
    }

    private void EnterDragonPhase(AiSkillList phase)
    {
        if (!IsDragonPhase(phase))
            return;

        if (IsDragonGroundPhase(phase))
        {
            ApplyDragonGroundState();
        }
        else
        {
            ApplyDragonFlightState();
            if (IsDragonPathPhase(phase))
            {
                Ai.PathHandler.AiPathSpeed = Ai.Owner.BaseMoveSpeed;
                if (!Ai.PathHandler.HasPathMovementData())
                    Logger.Error($"Dragon flight phase has no loaded path: Ai.Owner={Ai.Owner.ObjId}:{Ai.Owner.TemplateId}");
            }
        }

        CheckPipeName();
    }

    protected void ApplyDragonFlightState()
    {
        Ai.Owner.Buffs.RemoveBuff(DragonGroundBuffId);
        if (!Ai.Owner.Buffs.CheckBuff(DragonFlightBuffId))
            Ai.Owner.Buffs.AddBuff(DragonFlightBuffId, Ai.Owner);

        Ai.Owner.CanFly = true;
        Ai.Owner.CurrentGameStance = GameStanceType.Fly;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Combat;
        Ai.Owner.BroadcastPacket(new SCUnitModelPostureChangedPacket(Ai.Owner, Ai.Owner.AnimActionId, false), false);
    }

    private void ApplyDragonGroundState()
    {
        Ai.Owner.Buffs.RemoveBuff(DragonFlightBuffId);
        if (!Ai.Owner.Buffs.CheckBuff(DragonGroundBuffId))
            Ai.Owner.Buffs.AddBuff(DragonGroundBuffId, Ai.Owner);

        // Npc.CurrentGameStance intentionally forces every CanFly NPC back to Fly. Landing must
        // temporarily disable that capability before selecting the combat stance.
        Ai.Owner.CanFly = false;
        Ai.Owner.CurrentGameStance = GameStanceType.Combat;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Combat;
        Ai.Owner.StopMovement();
        Ai.Owner.BroadcastPacket(new SCUnitModelPostureChangedPacket(Ai.Owner, Ai.Owner.AnimActionId, false), false);
    }

    internal static int SelectPhaseIndex(
        IReadOnlyList<AiSkillList> phases,
        float healthRatio,
        double timeElapsed,
        int activePhaseIndex,
        bool sequential)
    {
        var selectedPhaseIndex = -1;
        for (var i = 0; i < phases.Count; i++)
        {
            if (!IsPhaseAvailable(phases[i], healthRatio, timeElapsed))
                continue;

            selectedPhaseIndex = i;
            break;
        }

        if (selectedPhaseIndex < 0)
            return activePhaseIndex;

        if (sequential && activePhaseIndex >= 0 && selectedPhaseIndex < activePhaseIndex)
            return activePhaseIndex;

        return selectedPhaseIndex;
    }

    internal static bool IsPhaseAvailable(AiSkillList phase, float healthRatio, double timeElapsed)
    {
        var healthMatches = phase.HealthRangeMin == 0 && phase.HealthRangeMax == 0 ||
                            phase.HealthRangeMin < healthRatio && healthRatio <= phase.HealthRangeMax;
        if (!healthMatches)
            return false;

        if (phase.TimeRangeStart == 0 && phase.TimeRangeEnd == 0)
            return true;
        if (phase.TimeRangeEnd == 0)
            return phase.TimeRangeStart <= timeElapsed;
        return phase.TimeRangeStart <= timeElapsed && timeElapsed <= phase.TimeRangeEnd;
    }

    internal static bool IsDragonPathPhase(AiSkillList phase)
    {
        return phase?.PipeName == DragonPathPipeName;
    }

    internal static bool IsDragonHoverPhase(AiSkillList phase)
    {
        return phase?.PipeName == DragonHoverPipeName || phase?.PhaseType == 2;
    }

    private static bool IsDragonGroundPhase(AiSkillList phase)
    {
        return phase?.PipeName == DragonGroundPipeName || phase?.PhaseType == 1;
    }

    private static bool IsDragonPhase(AiSkillList phase)
    {
        return IsDragonGroundPhase(phase) || IsDragonHoverPhase(phase) || IsDragonPathPhase(phase);
    }

    public override void Exit()
    {
        // Experimental handling of guards returning to their home position after chasing somebody
        // TODO: Fix walking animation
        if (Ai.Owner.AggroTable.IsEmpty && Ai.PathHandler.AiPathPointsRemaining.Count == 0)
        {
            Ai.PathHandler.TargetPosition = Vector3.Zero;
            Ai.Owner.CurrentAlertness = MoveTypeAlertness.Idle;
            Ai.Owner.CurrentGameStance = GameStanceType.Combat;
            Ai.PathHandler.AiPathPointsRemaining.Enqueue(new AiPathPoint
            {
                Action = AiPathPointAction.Speed,
                Param = "3",
                Position = Ai.HomePosition
            });
            Ai.GoToFollowPath();
        }

        _activePhase = null;
        _activePhaseIndex = -1;
        _queuePhaseEntrySkills = false;
        ResetPipeNameState();
        _enter = false;
    }
}
