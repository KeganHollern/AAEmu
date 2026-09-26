using System.Reflection;

using AAEmu.Game.Core.Managers;
using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class TaskManagerIntervalTests
{
    [Test]
    public async Task CallbackIntervalChange_UpdatesTheTriggerAlreadyChosenByTick()
    {
        var manager = CreateManager();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTrigger = DateTime.MinValue;
        var newTrigger = DateTime.MinValue;
        var accepted = false;
        var task = new CallbackTask(current =>
        {
            oldTrigger = current.TriggerTime;
            accepted = manager.UpdateRepeatInterval(current, TimeSpan.FromSeconds(1));
            newTrigger = current.TriggerTime;
            finished.SetResult();
        });
        manager.Schedule(task, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(2));
        try
        {
            typeof(TaskManager).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, [TimeSpan.FromMilliseconds(50)]);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            manager.Stop();
        }
        await Assert.That(accepted).IsTrue();
        await Assert.That(newTrigger).IsEqualTo(oldTrigger.AddSeconds(-1));
        await Assert.That(task.RepeatInterval).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(task.ExecuteCount).IsEqualTo(1);
        await Assert.That(manager.GetQueueCount()).IsEqualTo(1);
    }

    [Test]
    public async Task OldTask_CannotChangeTheIntervalOrCancelAReusedId()
    {
        var manager = CreateManager();
        var oldTask = new CallbackTask(_ => { });
        manager.Schedule(oldTask, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2));
        manager.Cancel(oldTask);
        var replacement = new CallbackTask(_ => { });
        manager.Schedule(replacement, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2));
        oldTask.Id = replacement.Id;
        oldTask.Cancelled = false;
        var trigger = replacement.TriggerTime;
        await Assert.That(manager.UpdateRepeatInterval(oldTask, TimeSpan.FromSeconds(1))).IsFalse();
        await Assert.That(manager.Cancel(oldTask)).IsFalse();
        await Assert.That(replacement.TriggerTime).IsEqualTo(trigger);
        await Assert.That(replacement.RepeatInterval).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(replacement.Cancelled).IsFalse();
        await Assert.That(manager.GetQueueCount()).IsEqualTo(1);
        manager.Stop();
    }

    [Test]
    public async Task IntervalChange_RejectsNonPositiveCancelledAndStoppedTasks()
    {
        var manager = CreateManager();
        var task = new CallbackTask(_ => { });
        manager.Schedule(task, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2));
        var trigger = task.TriggerTime;
        await Assert.That(manager.UpdateRepeatInterval(task, TimeSpan.Zero)).IsFalse();
        await Assert.That(manager.UpdateRepeatInterval(task, TimeSpan.FromSeconds(-1))).IsFalse();
        task.Cancelled = true;
        await Assert.That(manager.UpdateRepeatInterval(task, TimeSpan.FromSeconds(1))).IsFalse();
        task.Cancelled = false;
        manager.Stop();
        await Assert.That(manager.UpdateRepeatInterval(task, TimeSpan.FromSeconds(1))).IsFalse();
        await Assert.That(task.TriggerTime).IsEqualTo(trigger);
        await Assert.That(task.RepeatInterval).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    private static TaskManager CreateManager()
    {
        var tick = Mock.Of<ITickManager>();
        tick.OnTick.Returns(new TickManager.TickEventHandler());
        return new TaskManager(tick.Object);
    }

    private sealed class CallbackTask(Action<GameTask> callback) : GameTask
    {
        public override void Execute() => callback(this);
    }
}
