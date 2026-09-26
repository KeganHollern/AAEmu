// Authors: AAGene, ZeromusXYZ

using System.Collections.Concurrent;
using AAEmu.Commons.Utils;
using NCrontab;
using NLog;
using Task = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.Game.Core.Managers;

// ReSharper disable once ClassNeverInstantiated.Global
public class TaskManager(ITickManager tickManager) : Singleton<TaskManager>, ITaskManager
{
    private readonly ConcurrentDictionary<uint, Task> _queue = new();
    private readonly HashSet<uint> _taskIds = [];
    private readonly object _taskIdLock = new();
    private uint _taskIdIndex = 1;
    private readonly object _executionLock = new();
    private readonly AsyncLocal<bool> _executingTask = new();
    private int _activeExecutions;
    private bool _stopping;
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public static readonly CrontabSchedule.ParseOptions s_crontabScheduleParseOptions = new() { IncludingSeconds = true };

    public void Initialize()
    {
        // Preserve tasks queued by earlier lifecycle stages. Initialization must
        // not discard pending work; the field initializer already starts empty.
    }

    public void Start()
    {
        lock (_executionLock)
        {
            if (!_stopping)
                tickManager.OnTick.Subscribe(Tick, TimeSpan.FromMilliseconds(50), true);
        }
    }

    public void Stop()
    {
        // The host owns shutdown. A callback cannot wait for its own completion.
        if (_executingTask.Value)
            throw new InvalidOperationException("Stop the task manager outside its task callbacks.");

        lock (_executionLock)
        {
            _stopping = true;
            tickManager.OnTick.UnSubscribe(Tick);
            // Wait releases the gate, so active callbacks can finish and reject
            // their own follow-up schedules without a shutdown deadlock.
            while (_activeExecutions != 0)
                Monitor.Wait(_executionLock);
        }
    }

    private void Tick(TimeSpan delta)
    {
        lock (_executionLock)
        {
            if (_stopping)
                return;

            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();
            foreach (var (id, task) in _queue)
            {
                if (task.TriggerTime >= now)
                    continue;

                _activeExecutions++;
                _ = System.Threading.Tasks.Task.Run(() => ExecuteScheduledAsync(task));
                task.ExecuteCount++;

                // Check if there still needs to be executions done
                if (task.RepeatCount < 0 || task.ExecuteCount < task.RepeatCount)
                {
                    // If there is a CronSchedule set, use that to calculate the next TriggerTime
                    if (task.CronSchedule != null)
                        task.TriggerTime = task.CronSchedule.GetNextOccurrence(now);

                    // If there is an interval set, add it for the next TriggerTime
                    if (task.RepeatInterval != TimeSpan.Zero)
                        task.TriggerTime = now + task.RepeatInterval;

                    continue; // Don't remove this Task from the queue yet
                }

                toRemove.Add(id);
            }

            foreach (var objId in toRemove)
            {
                _queue.Remove(objId, out _);
                ReleaseId(objId);
            }
        }
    }

    /// <summary>
    /// Schedules a task to be executed in the future
    /// </summary>
    /// <param name="task">Task to Execute</param>
    /// <param name="startDelay">First trigger is startDelay time from now</param>
    /// <param name="repeatInterval">Time between Task Executions, needs to be set to allow usage of count</param>
    /// <param name="count">Number of times to repeat this action, -1 means infinite and 0 is the same as 1 time</param>
    /// <returns></returns>
    public bool Schedule(Task task, TimeSpan? startDelay = null, TimeSpan? repeatInterval = null, int count = -1)
    {
        lock (_executionLock)
        {
            if (_stopping)
                return false;
            var taskId = NextId();
            task.Id = taskId;

            // Preserve synchronous execution for an immediate, single invocation.
            if (startDelay == TimeSpan.Zero && count is >= 0 and <= 1)
                _activeExecutions++;
            else
            {
                task.TriggerTime = startDelay.HasValue ? DateTime.UtcNow + startDelay.Value : DateTime.UtcNow;
                if (repeatInterval.HasValue)
                {
                    task.RepeatInterval = repeatInterval.Value;
                    task.RepeatCount = count;
                }
                else
                    task.RepeatCount = 1;
                return _queue.TryAdd(taskId, task);
            }
        }

        ExecuteImmediate(task);
        return true;
    }

    /// <summary>
    /// Schedules a task to be executed in the future
    /// </summary>
    /// <param name="task">Task to Execute</param>
    /// <param name="cronExpression">Cron expression that defines the trigger conditions</param>
    /// <param name="startDelay">First trigger is only possible startDelay time from now</param>
    /// <param name="count">Number of times to repeat this action, -1 means infinite and 0 is the same as 1 time</param>
    /// <returns></returns>
    public bool CronSchedule(Task task, string cronExpression, TimeSpan? startDelay = null, int count = -1)
    {
        lock (_executionLock)
        {
            if (_stopping)
                return false;
            var taskId = NextId();
            task.Id = taskId;

            if (startDelay == TimeSpan.Zero)
                _activeExecutions++;
            else
            {
                var firstPossibleTriggerTime = startDelay.HasValue ? DateTime.UtcNow + startDelay.Value : DateTime.UtcNow;
                task.CronSchedule = CrontabSchedule.Parse(cronExpression, s_crontabScheduleParseOptions);
                task.TriggerTime = task.CronSchedule.GetNextOccurrence(firstPossibleTriggerTime);
                task.RepeatCount = count;
                return _queue.TryAdd(taskId, task);
            }
        }

        ExecuteImmediate(task);
        return true;
    }

    private void ExecuteImmediate(Task task)
    {
        var wasExecuting = _executingTask.Value;
        _executingTask.Value = true;
        try
        {
            task.Execute();
        }
        finally
        {
            _executingTask.Value = wasExecuting;
            ReleaseId(task.Id);
            FinishExecution();
        }
    }

    private async System.Threading.Tasks.Task ExecuteScheduledAsync(Task task)
    {
        _executingTask.Value = true;
        try
        {
            await task.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Scheduled task {TaskName} failed", task.Name);
        }
        finally
        {
            _executingTask.Value = false;
            FinishExecution();
        }
    }

    private void FinishExecution()
    {
        lock (_executionLock)
        {
            _activeExecutions--;
            if (_activeExecutions == 0)
                Monitor.PulseAll(_executionLock);
        }
    }

    /// <summary>
    /// Cancels a Task
    /// </summary>
    /// <param name="task"></param>
    /// <returns></returns>
    public bool Cancel(Task task)
    {
        var res = _queue.Remove(task.Id, out _);

        if (res)
        {
            task.Cancelled = true;
            ReleaseId(task.Id);
        }

        return res;
    }
    public void RemoveTasks(Func<Task, bool> predicate)
    {
        // Take a snapshot of the current tasks to avoid modifying the collection while iterating.
        foreach (var kvp in _queue.ToArray())
        {
            if (predicate(kvp.Value))
            {
                _queue.Remove(kvp.Key, out _);
                ReleaseId(kvp.Key);
            }
        }
    }
    private uint NextId()
    {
        lock (_taskIdLock)
        {
            var id = _taskIdIndex;
            while (_taskIds.Contains(id))
            {
                if (id == uint.MaxValue)
                    id = 1;
                else
                    id++;
            }
            _taskIds.Add(id);
            _taskIdIndex = id + 1u;
            if (_taskIdIndex == 0)
                _taskIdIndex = 1;

            return id;
        }
    }

    private void ReleaseId(uint id)
    {
        lock (_taskIdLock)
        {
            _taskIds.Remove(id);
        }
    }

    public int GetQueueCount()
    {
        return _queue.Count;
    }
}
