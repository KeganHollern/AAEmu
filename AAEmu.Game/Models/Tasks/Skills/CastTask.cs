using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Tasks.Skills;

public class CastTask(
    Skill skill,
    BaseUnit caster,
    SkillCaster casterCaster,
    BaseUnit target,
    SkillCastTarget targetCaster,
    SkillObject skillObject)
    : SkillTask(skill)
{
    public override void Execute()
    {
        if (Cancelled || Skill.Cancelled || caster is not Unit unit || !Skill.IsItemProc && !ReferenceEquals(unit.SkillTask, this))
            return;

        if (CastWindow != null && !CastWindow.TryComplete(DateTime.UtcNow, out var remaining))
        {
            // Tick can already have dequeued this task when damage extends the wait.
            // A fresh wakeup avoids reusing the scheduler ID of that queued callback.
            if (CastWindow.Active && remaining > TimeSpan.Zero)
                TaskManager.Instance.Schedule(new CastWakeupTask(this), remaining);
            return;
        }

        Skill.Cast(caster, casterCaster, target, targetCaster, skillObject);
    }
}

internal sealed class CastWakeupTask(CastTask cast) : Task
{
    public override void Execute()
    {
        cast.Execute();
    }
}
