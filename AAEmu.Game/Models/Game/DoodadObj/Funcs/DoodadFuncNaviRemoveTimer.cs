using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Doodads;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncNaviRemoveTimer : DoodadPhaseFuncTemplate
{
    public int After { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        if (After <= 0)
            return false;

        if (owner.OverridePhaseTime != DateTime.MinValue)
        {
            owner.PhaseTime = owner.OverridePhaseTime;
            owner.OverridePhaseTime = DateTime.MinValue;
        }
        if (owner.PhaseTime == DateTime.MinValue)
            owner.PhaseTime = DateTime.UtcNow;

        // The gate has different timers in its opening, open and closing phases.
        var deadline = owner.PhaseTime.AddMilliseconds(After);
        var task = new DoodadFuncFinalTask(caster, owner, 0, false, 0);
        owner.FuncTask = task;
        void Schedule()
        {
            var delay = deadline - DateTime.UtcNow;
            TaskManager.Instance.Schedule(task, delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        }
        if (SkillLaborBatch.Current is { } batch)
            batch.AfterCommit(Schedule);
        else
            Schedule();
        return false;
    }
}
