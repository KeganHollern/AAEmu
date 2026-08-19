using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Params;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class RunCommandSetBehavior : BaseCombatBehavior
{
    public override void Enter()
    {
        Ai.Owner.CurrentGameStance = GameStanceType.Combat;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Combat;
    }

    public override void Tick(TimeSpan delta)
    {
        // If everything is executed, and still time remaining, wait for it before going back to Neutral
        if (Ai.AiCurrentCommandRunTime > TimeSpan.Zero)
        {
            Ai.AiCurrentCommandRunTime -= delta;
            if (Ai.AiCurrentCommandRunTime <= TimeSpan.Zero)
            {
                // aaemu-cluster#92: the wait belonging to the command we already executed has
                // elapsed, so consume it. Advancing used to rely on the runtime landing NEGATIVE;
                // a wait that is an exact multiple of the tick delta (Timeout 1s at 100ms ticks, or
                // a 1000ms skill cooldown) lands on exactly zero, and the command was then executed
                // a second time, re-arming its own wait forever. Timeout has no failure path, so a
                // scripted sequence stalled on its first pause and never reached its later beats.
                Ai.AiCurrentCommand = null;
                Ai.AiCurrentCommandRunTime = TimeSpan.Zero;
            }

            return;
        }

        // If there are commands in the AI Command queue, execute those first
        if (Ai.AiCurrentCommand != null || Ai.AiCommandsQueue.Count > 0)
        {
            if (Ai.AiCurrentCommand == null)
            {
                Ai.AiCurrentCommand = Ai.AiCommandsQueue.Dequeue();
                Ai.AiCurrentCommandStartTime = DateTime.UtcNow;
            }

            TickCurrentAiCommand(Ai.AiCurrentCommand, delta);
            return;
        }

        Ai.GoToIdle();
        // Ai.GoToDefaultBehavior();
    }

    public override void Exit()
    {
        //
    }

    /// <summary>
    /// Ticks the current AI command
    /// </summary>
    /// <param name="aiCommand"></param>
    /// <param name="delta"></param>
    /// <exception cref="NotSupportedException"></exception>
    private void TickCurrentAiCommand(AiCommands aiCommand, TimeSpan delta)
    {
        if (Ai.AiCurrentCommandRunTime < TimeSpan.Zero)
        {
            Ai.AiCurrentCommand = null;
            Ai.AiCurrentCommandRunTime = TimeSpan.Zero;
            return;
        }

        // Check if we're still waiting
        if (Ai.AiCurrentCommandRunTime > TimeSpan.Zero)
        {
            Ai.AiCurrentCommandRunTime -= delta;
            return;
        }

        Logger.Debug($"{Ai.Owner.ObjId} ({Ai.Owner.TemplateId}) executing AI Command: {aiCommand.CmdId}, CommandSet: {aiCommand.CmdSetId}, P1: {aiCommand.Param1}, P2: {aiCommand.Param2}");
        // Execute command
        switch (aiCommand.CmdId)
        {
            case AiCommandCategory.FollowUnit:
                Logger.Warn($"AI Command: {aiCommand.CmdId} not implemented, NPC {Ai.Owner.ObjId} ({Ai.Owner.TemplateId}), CommandSet {aiCommand.CmdSetId}, P1 {aiCommand.Param1}, P2 {aiCommand.Param2}");
                break;
            case AiCommandCategory.FollowPath:
                {
                    // aaemu-cluster#92: a queued FollowPath has to load ITS OWN path file. The name used to
                    // be read before it was assigned, so the command loaded the previous command's path -
                    // or, for the first FollowPath in a set, an empty name (no points at all).
                    // Param1 selects the path slot: 1 = primary (walked once, then back to the command set),
                    // anything else = secondary (kept as the looping patrol route).
                    var isPrimaryPath = aiCommand.Param1 == 1;
                    var pathFileName = aiCommand.Param2;
                    if (isPrimaryPath)
                        Ai.AiFileName = pathFileName;
                    else
                        Ai.AiFileName2 = pathFileName;

                    if (!Ai.LoadAiPathPoints(pathFileName, isPrimaryPath))
                    {
                        // Nothing to walk: stay in the command set so the remaining commands still run
                        // instead of stranding the NPC in FollowPath forever.
                        Logger.Warn($"AI Command: {aiCommand.CmdId} has no usable path points, NPC {Ai.Owner.ObjId} ({Ai.Owner.TemplateId}), CommandSet {aiCommand.CmdSetId}, P1 {aiCommand.Param1}, P2 {aiCommand.Param2}");
                        break;
                    }

                    if (isPrimaryPath)
                    {
                        Ai.PathHandler.AiPathPointsRemaining.Enqueue(new AiPathPoint { Position = Vector3.Zero, Action = AiPathPointAction.ReturnToCommandSet, Param = string.Empty });
                    }

                    Ai.GoToFollowPath();
                    break;
                }
            case AiCommandCategory.UseSkill:
                Ai.AiSkillId = aiCommand.Param1;
                var owner = Ai.Owner; // capture once to avoid race with concurrent unit despawn
                var skillTemplate = SkillManager.Instance.GetSkillTemplate(Ai.AiSkillId);
                if (owner != null && skillTemplate != null && owner.UseSkill(Ai.AiSkillId, owner.CurrentTarget as Unit ?? owner) == SkillResult.Success)
                {
                    var coolDown = SkillManager.GetAttackDelay(skillTemplate, owner, false, 0.0);
                    // aaemu-cluster#92: a line must stay on screen long enough to read before the NEXT
                    // command replaces or interrupts it. Retail relied on the walk/wait between line
                    // skills for this; sets like 192 have no Timeout at all (the walk was the pause),
                    // and players reported the long entrance line being replaced too fast. So a cast
                    // that shows a chat bubble parks that bubble's reading time (66.7ms/char, the same
                    // sizing BubbleEffect uses) as its own execution time.
                    Ai.AiCurrentCommandRunTime = TimeSpan.FromMilliseconds(Math.Max(coolDown, ResolveBubbleDisplayMs(skillTemplate)));
                }
                break;
            case AiCommandCategory.Timeout:
                Ai.AiTimeOut = aiCommand.Param1;
                // ai_commands.param1 is SECONDS, not milliseconds: XL's own rows spell it out
                // ("2 sec", "2sec", "3 sec", "5 sec", "10 sec" all appear as param1 values), and the
                // range is 1-60. Applying it as milliseconds made every scripted pause a no-op.
                // This wait is ON TOP of whatever the previous command parked (a spoken line's
                // reading time): the authored Timeout is the dramatic pause, not the read time.
                Ai.AiCurrentCommandRunTime = TimeSpan.FromSeconds(Ai.AiTimeOut);
                break;
            default:
                throw new NotSupportedException(nameof(aiCommand.CmdId));
        }

        /*
        if (!string.IsNullOrEmpty(Ai.AiFileName))
        {
            if (Ai.Owner.IsInPatrol) { return; }

            Ai.Owner.IsInPatrol = true;
            Ai.Owner.Simulation.RunningMode = false;
            Ai.Owner.Simulation.Cycle = false;
            Ai.Owner.Simulation.MoveToPathEnabled = false;
            Ai.Owner.Simulation.MoveFileName = Ai.AiFileName;
            Ai.Owner.Simulation.MoveFileName2 = Ai.AiFileName2;
            Ai.Owner.Simulation.GoToPath(Ai.Owner, true, Ai.AiSkillId, Ai.AiTimeOut);
        }
        */

        if (Ai.AiCurrentCommandRunTime == TimeSpan.Zero)
            Ai.AiCurrentCommandRunTime = TimeSpan.FromSeconds(-1);
    }

    /// <summary>
    /// Longest reading time among the chat bubbles a skill shows, in milliseconds; 0 when it shows
    /// none. Sized exactly like BubbleEffect's display time so the served pacing and the client-side
    /// bubble lifetime agree. (aaemu-cluster#92)
    /// </summary>
    internal static double ResolveBubbleDisplayMs(SkillTemplate skillTemplate)
    {
        var displayMs = 0d;
        foreach (var skillEffect in skillTemplate.Effects)
        {
            if (skillEffect?.Template is BubbleEffect bubble)
            {
                var text = LocalizationManager.Instance.Get("bubble_effects", "speech", bubble.Id, string.Empty);
                displayMs = Math.Max(displayMs, BubbleEffect.ResolveDisplayMs(text));
            }
        }

        return displayMs;
    }

}
