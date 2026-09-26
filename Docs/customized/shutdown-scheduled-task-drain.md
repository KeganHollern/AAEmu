# Scheduled task drain during shutdown

This change supports cluster issue [#440](https://github.com/KeganHollern/aaemu-cluster/issues/440).
The previous `TaskManager.Stop` method did nothing.
Its tick callback could start tasks while the final save ran.
Tasks that already started could also change items after that save.
`ItemTimerTask`, for example, can remove expired inventory items.

## Shutdown contract

`TaskManager.Stop` closes admission and removes the scheduler's tick subscription.
It waits for every accepted execution to finish.
The count includes queued asynchronous executions and immediate synchronous executions.
An asynchronous execution remains active until its `ExecuteAsync` result completes.
Exceptions release the execution count through a `finally` block.

The admission gate protects both the stop decision and the execution count.
The wait releases that gate, so an active callback can finish without a lock cycle.
Its calls to `Schedule` or `CronSchedule` return false after shutdown starts.
A callback can still cancel a queued task.
A tick that was already queued cannot start another execution after shutdown starts.

The host must call `Stop` outside a task callback.
A call inside that callback throws instead of waiting for itself forever.
The host must also release locks that active callbacks need before it waits.

The manager stays closed for the rest of the process.
`Start` and `Initialize` do not reopen it.
A normal server restart creates a new manager in the new process.
`Initialize` still preserves tasks queued by earlier startup stages.
Future queued tasks do not run during shutdown.
Their queue entries remain available for normal cancellation by other managers.

The service must stop network admission and drain scheduled work before its final persistence checkpoint.
This change does not alter world despawn, tick handlers outside `TaskManager`, or the manual `ShutdownTask` command.
`WorldManager.Stop` must stay separate from this scheduler drain.

## Validation

The Release build passed with 0 errors.
All 87 focused scheduler, doodad timer, quest timer, and effect task tests passed.
The 7 new shutdown cases cover asynchronous completion, both immediate scheduling paths, late admission, queued ticks, exceptions, and self-wait prevention.
Against the old scheduler, 6 of those 7 cases failed.
The immediate-exception case also passed against the old scheduler.

The service integration tests must also check the final item and wallet checkpoint after network shutdown.
Keep the published release's reconnect and server restart checks in the shared `HUMAN VALIDATION` issue until the user completes them.
