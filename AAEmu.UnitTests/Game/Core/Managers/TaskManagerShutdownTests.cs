using System.Reflection;

using AAEmu.Game.Core.Managers;
using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class TaskManagerShutdownTests
{
    [Test]
    public async Task Stop_WaitsForScheduledAsyncCompletionAndRejectsItsFollowUp()
    {
        var (manager, stopping) = StartedManager();
        var entered = Signal();
        var release = Signal();
        var finished = false;
        var rescheduled = true;
        var later = new CallbackTask(() => throw new InvalidOperationException("A future task ran during shutdown."));
        manager.Schedule(later, TimeSpan.FromHours(1));
        var task = new AsyncCallbackTask(async () =>
        {
            entered.SetResult();
            await release.Task;
            manager.Cancel(later);
            rescheduled = manager.Schedule(new CallbackTask(() => { }), TimeSpan.Zero, count: 1);
            finished = true;
        });
        manager.Schedule(task, TimeSpan.FromSeconds(-1));
        Tick(manager);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = Task.Run(manager.Stop);
        try
        {
            await Task.WhenAny(stopping.Task, stop).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(stop.IsCompleted).IsFalse();
            await Assert.That(finished).IsFalse();
            await Assert.That(manager.Schedule(new CallbackTask(() => { }))).IsFalse();
            await Assert.That(manager.CronSchedule(new CallbackTask(() => { }), "* * * * * *")).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(finished).IsTrue();
        await Assert.That(rescheduled).IsFalse();
        await Assert.That(later.Cancelled).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stop_WaitsForImmediateCallbackWithoutHoldingItsAdmissionGate(bool cron)
    {
        var (manager, stopping) = StartedManager();
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var rescheduled = true;
        var finished = false;
        var task = new CallbackTask(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the immediate task.");
            rescheduled = manager.CronSchedule(new CallbackTask(() => { }), "* * * * * *", TimeSpan.Zero);
            finished = true;
        });
        var invocation = Task.Run(() => cron
            ? manager.CronSchedule(task, "* * * * * *", TimeSpan.Zero)
            : manager.Schedule(task, TimeSpan.Zero, count: 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = Task.Run(manager.Stop);
        try
        {
            await Task.WhenAny(stopping.Task, stop).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(stop.IsCompleted).IsFalse();
            await Assert.That(finished).IsFalse();
        }
        finally
        {
            release.Set();
            await invocation.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(finished).IsTrue();
        await Assert.That(rescheduled).IsFalse();
    }

    [Test]
    public async Task Stop_PreventsQueuedTicksAndStartFromReopeningAdmission()
    {
        var (manager, _) = StartedManager();
        var calls = 0;
        var task = new CallbackTask(() => calls++);
        manager.Schedule(task, TimeSpan.FromHours(1));
        manager.Stop();

        // A tick already queued by TickManager must not start an overdue task.
        task.TriggerTime = DateTime.MinValue;
        Tick(manager);
        manager.Initialize();
        manager.Start();
        Tick(manager);
        var rejected = new CallbackTask(() => calls++);
        await Assert.That(manager.Schedule(rejected, TimeSpan.Zero, count: 1)).IsFalse();
        await Assert.That(manager.CronSchedule(rejected, "* * * * * *", TimeSpan.Zero)).IsFalse();
        await Assert.That(rejected.Id).IsEqualTo(0u);
        await Assert.That(calls).IsEqualTo(0);
        manager.Stop();
    }

    [Test]
    public async Task Stop_DrainsAScheduledCallbackEvenWhenItsAsyncBodyThrows()
    {
        var (manager, stopping) = StartedManager();
        var entered = Signal();
        var release = Signal();
        var task = new AsyncCallbackTask(async () =>
        {
            entered.SetResult();
            await release.Task;
            throw new InvalidOperationException("Expected scheduled test failure.");
        });
        manager.Schedule(task, TimeSpan.FromSeconds(-1));
        Tick(manager);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = Task.Run(manager.Stop);
        try
        {
            await Task.WhenAny(stopping.Task, stop).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(stop.IsCompleted).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task ImmediateException_ReleasesExecutionBeforeHostStop()
    {
        var (manager, _) = StartedManager();
        Assert.Throws<InvalidOperationException>(() => manager.Schedule(
            new CallbackTask(() => throw new InvalidOperationException("Expected immediate test failure.")),
            TimeSpan.Zero, count: 1));

        await Task.Run(manager.Stop).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task CallbackCannotWaitForItsOwnStop_ButHostCanStillStop()
    {
        var (manager, _) = StartedManager();
        manager.Schedule(new CallbackTask(() => Assert.Throws<InvalidOperationException>(manager.Stop)),
            TimeSpan.Zero, count: 1);

        await Task.Run(manager.Stop).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static (TaskManager Manager, TaskCompletionSource Stopping) StartedManager()
    {
        var tick = Mock.Of<ITickManager>();
        var handler = new TickManager.TickEventHandler();
        var stopping = Signal();
        var accesses = 0;
        tick.OnTick.Returns(() =>
        {
            if (Interlocked.Increment(ref accesses) > 1)
                stopping.TrySetResult();
            return handler;
        });
        var manager = new TaskManager(tick.Object);
        manager.Start();
        return (manager, stopping);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Tick(TaskManager manager) => typeof(TaskManager)
        .GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(manager, [TimeSpan.Zero]);

    private sealed class CallbackTask(Action callback) : GameTask
    {
        public override void Execute() => callback();
    }

    private sealed class AsyncCallbackTask(Func<Task> callback) : GameTask
    {
        public override void Execute() => throw new InvalidOperationException("Use the asynchronous task body.");
        public override Task ExecuteAsync() => callback();
    }
}
