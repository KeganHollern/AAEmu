# Economy persistence foundation review

This branch preserves the accepted shared save implementation for issues
[303](https://github.com/KeganHollern/aaemu-cluster/issues/303),
[305](https://github.com/KeganHollern/aaemu-cluster/issues/305),
[306](https://github.com/KeganHollern/aaemu-cluster/issues/306), and
[308](https://github.com/KeganHollern/aaemu-cluster/issues/308).
It is a review checkpoint with missing dependencies, not a merge or release candidate.

## Accepted implementation

`PersistenceSaveContext` and additive manager save overloads bind item, container,
mail, auction, and character writes to one transaction. `TryCommitEconomy` saves
pending economic state and all online characters, includes explicit participants
such as a departing character, and supports a caller's final SQL writes.

Dirty flags and written deletion queues are acknowledged after commit. Depleted
items are deleted during the transaction while runtime ID release remains the
settlement caller's responsibility. Known failures before commit return false;
commit or acknowledgment exceptions propagate without claiming a rollback outcome.
`Character.SaveDirectlyToDatabase` joins this checkpoint instead of independently
saving a wallet. No schema migration or production database operation is included.

## Approval and dependency limits

Compilation requires six additional `Save(PersistenceSaveContext)` overloads for
`CharacterPortals`, `CharacterFriends`, `CharacterBlocked`, `CharacterSkills`,
`CharacterQuests`, and `CharacterMates`. These serializers currently clear deletion
queues before their surrounding transaction commits. The proposed changes retain
those queues until commit and acknowledge only the IDs actually written.

Automatic approval review rejected both attempts to apply these six changes,
stating that they broadened the approved economy work into unrelated character
subsystems. The user has not yet answered the explicit follow-up approval request.
None of those rejected serializer edits are present in this branch. The accepted
character save caller deliberately retains its pending overload dependencies;
there are no substitute implementations or stubs.

## Validation

`git diff --check` passes. The integration-project build restores successfully,
then stops at six missing-overload compiler errors in `Character.Save`. There were
no other Game compiler errors in that attempt; five existing analyzer warnings
remain. The integration tests have not compiled or run.

`EconomyPersistenceTests` is authored for the existing disposable GameMySql
fixture. It covers complete economic rollback and retry, SQL failures in each
writer, failure on a later item, deletion-queue acknowledgment, explicit offline
participants, commit-connection failure, committed container deregistration, and
the seven deletion queues across the six pending character serializers.

Local Docker's API version is older than the fixture's Testcontainers requirement.
After approval and dependency completion, compile both test projects, run the
focused unit checks and hosted GameMySql fixture, and review the integrated feature
branches before merging or publishing. No release or completed validation is
claimed by this checkpoint.
