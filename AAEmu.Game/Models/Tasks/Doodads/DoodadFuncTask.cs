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
        var executed = false;
        if (_taskOwner?.Spawner is { } spawner)
            spawner.ExecutePhaseTask(_taskOwner, this, () =>
            {
                executed = true;
                ExecuteCurrent();
            });
        else if (ReferenceEquals(_taskOwner?.FuncTask, this))
        {
            executed = true;
            ExecuteCurrent();
        }

        if (!executed)
            OnRetired();
    }

    internal void Retire()
    {
        Cancel();
        // A dispatched callback is already absent from the queue, so Cancel may
        // return false without invoking OnCancel. Its resources still belong here.
        OnRetired();
    }

    public override void OnCancel() => OnRetired();

    protected virtual void OnRetired() { }

    protected void ClearCurrentTask()
    {
        if (ReferenceEquals(_taskOwner.FuncTask, this))
            _taskOwner.FuncTask = null;
    }

    protected abstract void ExecuteCurrent();
}
