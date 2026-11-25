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
    internal ShipRepairCast Repair { get; } = ShipRepairCast.Start(skill, caster, target);

    internal ShipCargoPickupCast CargoPickup { get; } = ShipCargoPickupCast.Start(skill, caster, target);

    public override void Execute()
    {
        if (Cancelled || Skill.Cancelled || caster is not Unit unit || !Skill.IsItemProc && !ReferenceEquals(unit.SkillTask, this))
        {
            Skill.ReleaseSkippedDoodadCast();
            return;
        }

        if (CastWindow != null && !CastWindow.TryComplete(DateTime.UtcNow, out var remaining))
        {
            // Tick can already have dequeued this task when damage extends the wait.
            // A fresh wakeup avoids reusing the scheduler ID of that queued callback.
            if (CastWindow.Active && remaining > TimeSpan.Zero)
                TaskManager.Instance.Schedule(new CastWakeupTask(this), remaining);
            return;
        }

        if ((Repair != null && !Repair.CanContinue(unit)) || (CargoPickup != null && !CargoPickup.CanContinue(unit)))
        {
            Skill.Stop(unit);
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
