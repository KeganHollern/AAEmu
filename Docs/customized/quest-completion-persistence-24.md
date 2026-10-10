# Quest completion persistence

Source issue: [aaemu-cluster #24](https://github.com/KeganHollern/aaemu-cluster/issues/24).
The [scope comment](https://github.com/KeganHollern/aaemu-cluster/issues/24#issuecomment-5471036136) identifies the shared transaction needed for quest and achievement progress.

## Confirmed defect

The old Reward transition saves the completed quest block before achievement progress and active quest removal.
These writes use separate database operations.
A failed removal can leave a completed bit and achievement progress beside an active quest row.
The old code also removes the quest from memory after that failed removal.
A later reconnect can load inconsistent quest state.

Repeatable quest records can change without a new achievement completion.
The old achievement path can leave those record changes pending until the next character save.
A server stop before that save can lose a repeatable completion count.

These defects support the correction below.
They do not establish the exact cause of the original reconnect report.
The current Game logs contain no quest write-through failure or achievement persistence failure.

## Completion transaction

The Reward transition uses `CharacterQuests.TryCompleteQuest` under `SaveManager.PersistenceSyncRoot`.
It prepares a new completed block and defers achievement persistence and notifications.
One MySQL transaction removes the exact active attempt, saves the completed block, and saves achievement state.
The transaction includes repeatable record changes even when no achievement completes.

A known aborted transaction restores achievement state and keeps the active Reward attempt for retry.
The normal report path can retry settlement with the saved reward selection.
The retry checks the authored report source and the normal interaction range.
A restored Reward step supplies proof that the earlier report phase passed.
The retry does not run reward side effects again.
After a confirmed commit, the server installs the completed block and removes the active quest from memory.
Cleanup, quest events, and client notifications follow that state change.
The exact runtime quest object prevents a repeated request from applying completion again.

## Uncertain commit

The transaction records whether the exact attempt row exists before its writes.
If a commit reply fails, a locked database read waits for the transaction outcome.
An unchanged source row means that settlement did not commit.
A removed source row and the expected completed block confirm the shared commit.
The old completed bit alone cannot prove a new repeatable completion.

Some quests complete before their first active-row save.
That absent source cannot prove the outcome of an uncertain commit.
The server then uses `SaveManager.FailForConsistency` and restarts from durable state.
It preserves prepared state until the process stops, so a later save cannot overwrite an unknown result.

The completion path does not write a separate current-state checkpoint before settlement.
Such a checkpoint can save cleared reward flags before the corresponding reward assets reach the database.
Quest reward delivery keeps its current transaction paths.
This correction covers completion state and achievement progress.

## Automated checks

The old-release DELETE-failure test fails because the completed bit remains saved beside the active quest row.
The correction tests real MySQL failures in active-row removal, completed blocks, achievement records, and completed achievements.
Commit tests cover a failed reply after commit, a failed reply after rollback, and a failure before success packets.
Tests also cover repeatable counts, attempts without an accept row, memory restoration, and the persistence stop for unknown outcomes.
Unit tests check concurrent repeated requests, cleanup failures, packet order, and valid report retries after restore.

The Release build and Game script compiler passed.
The local run passed 5,993 unit tests and skipped 20 optional asset checks.
All 557 Game MySQL tests, 50 Content Studio tests, and 16 quest audit tests passed.
The MySQL run includes 14 new quest completion cases.
Independent source review found no blocker in transaction outcomes, packets, cleanup, or restored report retries.

## Human checks

Use the current launcher-managed client and a quest available through normal character progress.
Complete the quest, reconnect, and check its completed state and its reward counts.
For an available repeatable quest, complete 2 separate attempts and reconnect after each attempt.
Check the saved quest record count when the client does not show it.
Use the next normal Game restart to check the same saved state again.
Do not start a separate restart only for this check.

Record each result in [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
Automated failure tests do not complete these human checks.
