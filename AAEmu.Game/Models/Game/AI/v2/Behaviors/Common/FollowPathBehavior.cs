using System.Numerics;

using AAEmu.Game.Models.Game.AI.v2.Params.Almighty;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class FollowPathBehavior : BaseCombatBehavior
{
    public AlmightyNpcAiParams _aiParams;
    private bool _enter;

    public override void Enter()
    {
        Ai.Owner.InterruptSkills();
        _skillQueue = new Queue<AiSkill>();
        Ai.Owner.CurrentGameStance = GameStanceType.Relaxed;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Idle;

        _combatStartTime = DateTime.UtcNow;

        if (Ai.Owner is { IsInBattle: false } npc)
        {
            npc.Events.OnCombatStarted(this, new OnCombatStartedArgs { Owner = npc, Target = npc });
        }
        Ai.Param = Ai.Owner.Template.AiParams;

        // aaemu-cluster#92: path movement is driven by Ai.PathHandler in Tick(). The legacy Simulation
        // route used to be started here as well, but GoToPath() toggles MoveToPathEnabled, so this call
        // actually broadcast a StopMovement the moment a queued FollowPath began - and when it did move,
        // it fought the path handler for the same NPC.
        Ai.Owner.IsInPatrol = true;

        _enter = true;
    }

    public override void Tick(TimeSpan delta)
    {
        if (!_enter)
            return; // not initialized yet Enter()

        //if (Ai.Param is not AlmightyNpcAiParams aiParams)
        //   return;

        //_aiParams = aiParams;

        if (!UpdateTarget())
            Ai.Owner.SetTarget(null);

        if (CheckAggression())
            return;

        if (CheckAlert())
            return;

        //var targetDist = Ai.Owner.GetDistanceTo(Ai.Owner.CurrentTarget);
        //PickSkillAndUseIt(SkillUseConditionKind.InIdle, Ai.Owner, targetDist);

        // If still aggro, go back to combat
        if (Ai.Owner.IsInBattle && !Ai.Owner.AggroTable.IsEmpty)
        {
            Ai.GoToCombat();
            return;
        }

        var hasPathMovementLeft = Ai.PathHandler.RunCurrentPath(delta);

        // aaemu-cluster#92: a ReturnToCommandSet path point switches behavior from inside RunCurrentPath.
        // The idle fallbacks below would immediately kick the NPC out of the command set it just resumed,
        // so the queued commands after the FollowPath (typically the self-despawn skill) never ran.
        if (!ReferenceEquals(Ai.GetCurrentBehavior(), this))
            return;

        if (!hasPathMovementLeft)
        {
            Ai.GoToIdle();
            return;
        }

        if (Ai.PathHandler.TargetPosition == Vector3.Zero && Ai.PathHandler.AiPathPoints.Count <= 0 && Ai.PathHandler.AiPathPointsRemaining.Count <= 0)
        {
            Ai.GoToIdle();
        }

        /*
        CheckPipeName();
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
                RefreshSkillQueue(_aiParams.AiPathSkillLists);
                RefreshSkillQueue(_aiParams.AiSkillLists);
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

        var healthRatio = (float)Ai.Owner.Hp / Ai.Owner.MaxHp * 100;
        if (!(healthRatio <= 80f))
            return;
        */

        /*
        Ai.Owner.IsInPatrol = false;
        Ai.Owner.Simulation.MoveToPathEnabled = false;
        Ai.Owner.StopMovement();
        Ai.GoToDefaultBehavior();
        */
    }

    public override void Exit()
    {
        // aaemu-cluster#92: StopMove() used to clear this as a side effect of the removed Simulation call.
        // It has to be released here, or the NPC would refuse every later scripted route.
        Ai.Owner.IsInPatrol = false;
        _enter = false;
    }

    /* Unused Code (May be removed later)
    private bool RefreshSkillQueue(List<AiSkillList> skillLists)
    {
        var targetDist = Ai.Owner.GetDistanceTo(Ai.Owner.CurrentTarget);
        var aiSkillLists = RequestAvailableAiSkillList(skillLists);
        if (aiSkillLists.Count > 0)
        {
            // select a set of skills by dice
            var selectedSkillList = aiSkillLists.RandomElementByWeight(s => s.Dice);
            if (selectedSkillList != null)
            {
                _aiParams.RestorationOnReturn = selectedSkillList.Restoration;
                _aiParams.GoReturnState = selectedSkillList.GoReturn;

                // add startAiSkill first to the queue if it is available
                if (selectedSkillList.StartAiSkills.Count > 0)
                {
                    foreach (var skill in selectedSkillList.StartAiSkills)
                    {
                        if (Ai.Owner.Cooldowns.CheckCooldown(skill.SkillId))
                        {
                            continue;
                        }
                        _skillQueue.Enqueue(skill);
                    }
                }

                var availableSkillList = RequestAvailableSkillList(selectedSkillList.SkillLists);

                // then add from skillLists
                var skillList = availableSkillList.RandomElementByWeight(s => s.Dice);
                if (skillList != null)
                {
                    foreach (var skill in skillList.Skills)
                    {
                        if (Ai.Owner.Cooldowns.CheckCooldown(skill.SkillId))
                        {
                            continue;
                        }
                        var template = SkillManager.Instance.GetSkillTemplate(skill.SkillId);
                        if (template == null) { continue; }
                        if (targetDist >= template.MinRange && targetDist <= template.MaxRange || template.TargetType == SkillTargetType.Self)
                        {
                            _skillQueue.Enqueue(skill);
                        }
                    }
                }
            }

            return _skillQueue.Count > 0;
        }

        if (Ai.Owner.Template.BaseSkillId == 0) { return false; }

        var item = new AiSkill();
        item.SkillId = (uint)Ai.Owner.Template.BaseSkillId;
        item.Strafe = Ai.Owner.Template.BaseSkillStrafe;
        item.Delay = Ai.Owner.Template.BaseSkillDelay;
        _skillQueue.Enqueue(item);

        return true;

    }
    
    private List<AiSkillList> RequestAvailableAiSkillList(List<AiSkillList> aiSkillLists)
    {
        var healthRatio = (int)((float)Ai.Owner.Hp / Ai.Owner.MaxHp * 100);

        var baseList = aiSkillLists.AsEnumerable();
        var timeElapsed = (DateTime.UtcNow - _combatStartTime).TotalSeconds;

        var availableSkillLists = new List<AiSkillList>();
        foreach (var s in baseList)
        {
            // first, let's select the allowed skills based on life value
            if ((s.HealthRangeMin == 0 && s.HealthRangeMax == 0) || (s.HealthRangeMin < healthRatio && healthRatio <= s.HealthRangeMax))
            {
                // then, select the allowed skills by time
                if ((s.TimeRangeStart >= 0 && s.TimeRangeEnd > 0) || (s.TimeRangeStart > 0 && s.TimeRangeEnd >= 0))
                {
                    if (s.TimeRangeStart <= timeElapsed && s.TimeRangeEnd == 0)
                    {
                        availableSkillLists.Add(s);
                    }
                    else if (s.TimeRangeStart <= timeElapsed && timeElapsed <= s.TimeRangeEnd)
                    {
                        availableSkillLists.Add(s);
                    }
                }
                else if (s.TimeRangeStart == 0 && s.TimeRangeEnd == 0)
                {
                    availableSkillLists.Add(s);
                }
            }
        }

        return availableSkillLists;
    }

    private List<SkillList> RequestAvailableSkillList(List<SkillList> skillLists)
    {
        var healthRatio = (int)((float)Ai.Owner.Hp / Ai.Owner.MaxHp * 100);

        var baseList = skillLists.AsEnumerable();
        var timeElapsed = (DateTime.UtcNow - _combatStartTime).TotalSeconds;

        var availableSkillLists = new List<SkillList>();
        foreach (var s in baseList)
        {
            // first, let's select the allowed skills based on life value
            if ((s.HealthRangeMin == 0 && s.HealthRangeMax == 0) || (s.HealthRangeMin < healthRatio && healthRatio <= s.HealthRangeMax))
            {
                // then, select the allowed skills by time
                if ((s.TimeRangeStart >= 0 && s.TimeRangeEnd > 0) || (s.TimeRangeStart > 0 && s.TimeRangeEnd >= 0))
                {
                    if (s.TimeRangeStart <= timeElapsed && s.TimeRangeEnd == 0)
                    {
                        availableSkillLists.Add(s);
                    }
                    else if (s.TimeRangeStart <= timeElapsed && timeElapsed <= s.TimeRangeEnd)
                    {
                        availableSkillLists.Add(s);
                    }
                }
                else if (s.TimeRangeStart == 0 && s.TimeRangeEnd == 0)
                {
                    availableSkillLists.Add(s);
                }
            }
        }

        return availableSkillLists;
    }
    */

}
