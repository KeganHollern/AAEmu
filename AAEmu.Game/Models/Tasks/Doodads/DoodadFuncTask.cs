using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Tasks.Doodads;

public abstract class DoodadFuncTask : Task
{
    private readonly Doodad _taskOwner;

    protected DoodadFuncTask(BaseUnit caster, Doodad owner, uint skillId)
    {
        _taskOwner = owner;
    }

    public sealed override void Execute()
    {
        // Cancellation can race the task runner after it has dispatched a callback.
        // The occurrence must still own this task when its phase effects execute.
        if (_taskOwner?.Spawner is { } spawner)
            spawner.ExecutePhaseTask(_taskOwner, this, ExecuteCurrent);
        else if (ReferenceEquals(_taskOwner?.FuncTask, this))
            ExecuteCurrent();
    }

    protected void ClearCurrentTask()
    {
        if (ReferenceEquals(_taskOwner.FuncTask, this))
            _taskOwner.FuncTask = null;
    }

    protected abstract void ExecuteCurrent();
}
