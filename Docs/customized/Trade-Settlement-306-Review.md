# Player trade settlement review

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/306>

This branch preserves the authored trade implementation and regression tests.
It has unresolved dependencies and is not a merge or release candidate.

## Implemented behavior

A trade requires a matching invitation and exact current character identities.
Participants must be online, alive, idle, friendly, in the same world and instance,
and within the server's five-meter 3D interaction range. Each player can have one
pending invitation or active trade. Unsolicited and replayed accepts are ignored.

Offers reserve registered inventory items and available money. Item snapshots use
the offered quantity, so using an unreserved stack remainder remains valid. Offer
changes or unlocking clear both locks and confirmations; both players must lock
before confirming. Final validation checks the offered identities, details,
locations, remaining quantities, and balances again.

Settlement retires the trade under the persistence lock, stages outgoing items
before incoming items to allow full-bag exchanges, stages both wallets, then calls
the shared economy checkpoint. Known save failures restore prepared state.
Uncertain commit and completion-notification exceptions preserve it. Completion
tasks are frozen before callbacks and sent after commit. Existing packet task
limits are checked before saving.

All nine trade packet handlers tolerate a missing active character. Disconnect and
leave-world paths cancel using exact character identity. Cancellation retires the
trade before notifications and logs each failed notification independently so
logout and the other player's notification can continue.

## Dependency and approval limits

The reviewed reservation and inventory helpers are included as `ef4e9dc0f`, a
cherry-pick of `907a52a50`. The branch still requires:

- The shared economy persistence foundation and its six pending character save
  serializer approvals, which provide `ISaveManager.TryCommitEconomy`.
- The pending `Skill.IsExecuting(Character)` guard. Automatic approval review
  rejected the proposed core skill and asynchronous plot wrappers; none of those
  `Skill.cs` changes or substitute stubs are included here.
- Craft reservation enforcement from `f243c21a9`, to prevent crafting from starting
  against reserved trade assets. It is not included in this checkpoint.

These dependencies must be resolved without weakening the final skill and craft
exclusion guarantees before this issue is considered complete.

## Validation

`git diff --check` passes. A unit-project build restored successfully and stopped
at exactly two missing Game APIs: `ISaveManager.TryCommitEconomy` and
`Skill.IsExecuting`. There were no other Game compiler errors in that attempt;
five existing analyzer warnings remain. The new unit and integration tests have
not compiled or run.

`TradeSettlementTests` covers invitation and eligibility rules, invalid or changed
offers, partial stacks and unreserved consumption, full bags, money overflow,
lock and confirmation resets, known rollback and fresh retry, uncertain saves,
failed notifications, concurrent and reentrant confirmations, expiry, stale
character identity, and packet guards.

`TradePersistenceTests` uses actual trade, inventory, character, and save code in
the existing disposable GameMySql fixture. It asserts both wallets and original
and split item identities, ownership, counts, grades, and containers using fresh
SQL connections and the production `ItemManager.LoadUserItems` loader. It includes
SQL failures on the source item, split item, and recipient character; fresh retry;
concurrent final confirmation; and nonthrowing item registration failure.

After dependencies are available, compile and execute these tests and run hosted
GameMySql validation. Human checks after publication should use two current
clients to exercise partial offers, revised locks, full bags, simultaneous
confirmation, disconnect cancellation, and inventory and wallet state after
relogging. No human validation, image publication, or deployment is claimed here.
