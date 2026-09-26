# Item persistence and pickup limits

This record covers cluster issues #440 and #471.
The source base is `b11df2e729055d61098d0660f153882b204c7876`.

## Disconnect and shutdown

The current disconnect path already uses `SaveManager.TryCommitEconomy`.
It saves the departing character, items, containers, and mates in one transaction.
The old shutdown order still ran the final checkpoint before network shutdown.

Game shutdown now closes connection admission and marks accepted connections closed.
It stops the listener and waits for each active packet and disconnect callback.
The session lock prevents the final disconnect save from overtaking an active packet.
The scheduler then stops before the final checkpoint.
World removal stays after that checkpoint.

The MySQL test holds an active packet while network shutdown starts.
The packet changes a wallet and an item count before it releases the session lock.
Shutdown waits, saves the final values, and removes the connection.
The production item loader reads the expected item count from a new manager.
The earlier duplicate-login test also checks the disconnect transaction.

This change addresses normal hosted shutdown and disconnect persistence.
It does not make an abrupt process kill equivalent to a completed save.

## Held item quantities

`ItemPickupPolicy` counts each template across the character's bag, bank, and equipment.
It checks the full incoming quantity before a grant, stack merge, or external move.
An internal move does not acquire another item.
Mail and auction attachments do not count until the character claims them.
The check uses prepared transaction counts, including earlier consumes and grants.
Rejection sends `ItemPickupLimit` and leaves source items and payment unchanged.

The shared mutation and ordinary container paths use the policy.
The auction claim planner checks the limit before its database commit.
The achievement planner uses its normal mail fallback when inventory delivery exceeds the limit.
Saved equipment and saved over-limit quantities remain intact during startup.

Tests cover quest items, backpacks, bank and equipment totals, multiple prepared grants,
consumed replacements, internal moves, mail claims, coffer transfers, and payment restoration.
Auction tests check that rejection makes no checkpoint, receipt, or payment change.
Achievement tests cover stacks, new slots, and mail fallback.

No SQL schema, compact snapshot, client content, or packet body changes.

## Pending human checks

The shared backlog is [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).

- Change a cheap item and reconnect. Check the exact item and money counts.
- Before an agreed normal server restart, record bag, bank, equipment, and mate gear.
  Reconnect after restart and check the recorded state.
- With a confirmed limited item, reach its authored limit across held containers.
  Try another normal acquisition and check the error and unchanged payment.
- Move that item between the bag and bank. Check that a legal internal move succeeds.
- Try an available auction return claim at the limit. Check that its attachment remains.
  Free enough capacity, claim once, and check the final count after reconnect.
- If a suitable achievement is available, check that its limited reward arrives through mail.
  Check the reward and claim state after reconnect.

Automated tests cover race conditions. Human checks do not replace those tests.
