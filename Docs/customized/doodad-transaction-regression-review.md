# Doodad transaction regression review

This review compares deployed source `d20077541c3509ab2dfacde5a6ffce1cf29805e6` with the earlier economy and occurrence logic.
It follows the failed mining check in [cluster issue 567](https://github.com/KeganHollern/aaemu-cluster/issues/567).
[Cluster issue 569](https://github.com/KeganHollern/aaemu-cluster/issues/569) records the 2 corrections from this review.

## Due tasks on player plants

Commit `647b1fe497857b3bc7b16d7ee2411bd3181b9c46` checks task ownership before a phase task runs.
Authored spawns use `DoodadSpawner.ExecutePhaseTask`, which holds `SaveManager.PersistenceSyncRoot`.
Player-placed and loaded plants can have no spawner.
Their ownership check did not hold that lock.

Commit `4f058388aefe5822604c970c4cbb091c8f2802c7` added reversible phase changes to paid-skill transactions.
The transaction can temporarily remove or replace `FuncTask` before the database accepts the change.
A due callback on another thread could see this temporary state and retire.
The task runner already removed that callback from its queue.
A failed transaction restored the old reference, but its task could no longer run.

`DoodadFuncTask.Execute` now holds the persistence lock through the ownership check and task effects.
A callback waits until the transaction succeeds or restores the previous state.
After failure, the restored task can run.
After success, the old task retires without changing the new phase.

`PaidPhaseChange_DueGrowthCallbackWaitsForCommitOutcome` checks both outcomes with the real task runner and growth handler.
The test uses plant template `322`, growth transition `233 -> 234`, and the competing uproot transition to `8089`.
It checks phase, labor, task effects, retirement, and queue cleanup.

## Phase event identity

Before `4f058388`, world phase events received the phase ID at each transition.
The transaction change delayed those events but read the mutable phase ID after commit.
For the Iron Vein sequence, events reported `17070, 17070` instead of `3150, 17070`.
The earlier packet correction captured the client packet but left this separate event defect.

`Doodad.DoChangePhaseLocked` now captures the event phase ID with the packet.
World scripts receive the phase that caused each event.
The doodad object retains its final committed state, and area sensors use that current state.
The correction does not replay old object state or publish events from a failed transaction.

`MiningCompletion_PreservesTheBrokenVeinPhaseOnlyAfterCommit` checks the packet sequence, event sequence, committed object state, and failed transaction.

## Mining comparison and limits

The review also compared the source before `647b1fe4` and before phase-cycle change `2dc1abc0`.
The creation packet, transform code, authored Iron Vein positions, and normal removal delay did not change in those ranges.
Housing geometry reads those transforms but does not change the authored vein position.
The compact data still sets normal Iron Vein removal to 3 seconds, followed by its respawn delay.

The lost intermediate client phase was a proven regression from `4f058388`.
Commit `185fe922d1ac5ebd66694a226143e24d791217b4`, merged as `d2007754`, corrected that packet defect.
The user then reported a brief gap, some incorrect rubble positions, and later removal.
The static comparison does not prove a cause for the gap or position report.
The 2 corrections here do not claim to resolve those visual reports.
Issue 567 remains open for that evidence gap.

The review checked harvest grants, craft permissions, saved mate equipment, and other deferred skill notifications.
It found no further proven regression in those paths.
The earlier saved mate equipment defect already has a correction in `47623df5f885bc52875163618d1697ef6bbe2b8d`.
A delayed Clout ordering difference lacks a confirmed paid path in the inspected compact data.
That candidate does not justify a behavior change in this release.

## Validation scope

Before the corrections, both task-race cases and the successful phase-event case failed.
The failed-transaction phase case passed.
The source changes need no SQL update, compact change, client patch, or launcher change.
The source PR records the final focused and CI results.
Manual mining validation remains incomplete.
