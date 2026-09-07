using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using NLog;

namespace AAEmu.Game.Models.Tasks.Doodads;

public class DoodadFuncCloutTask(BaseUnit caster, Doodad owner, uint skillId, int nextPhase, AreaTrigger areaTrigger, Doodad taskOwner)
    : DoodadFuncTask(caster, taskOwner, skillId)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly BaseUnit _caster = caster;
    private readonly Doodad _owner = owner;
    private readonly uint _skillId = skillId;
    private int _triggerRemoved;

    protected override void ExecuteCurrent()
    {
        if (Logger.IsTraceEnabled)
            Logger.Trace("[Doodad] DoodadFuncCloutTask: Doodad {0}, TemplateId {1}. Using skill {2} with doodad phase {3}", _owner.ObjId, _owner.TemplateId, _skillId, nextPhase);

        ClearCurrentTask();

        RemoveTrigger();
        if (nextPhase == -1)
            _owner.Delete();

        _owner.DoChangePhase(_caster, nextPhase);
    }

    protected override void OnRetired() => RemoveTrigger();

    private void RemoveTrigger()
    {
        if (Interlocked.Exchange(ref _triggerRemoved, 1) != 0)
            return;

        areaTrigger.Owner?.AttachAreaTriggers.Remove(areaTrigger);
        AreaTriggerManager.Instance.RemoveAreaTrigger(areaTrigger);
    }
}
