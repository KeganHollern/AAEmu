# Conflict-zone lifecycle

Resolves [aaemu-cluster #283](https://github.com/KeganHollern/aaemu-cluster/issues/283).
Reviewed starting source: `ff4a243a61a98e785f92e651f7bef7f1d746ae38`.

Qualifying hostile player kills now reach the conflict counter during Tension,
Danger, Dispute, Unrest, and Crisis. The counter retains the existing cumulative
thresholds and strict `>` comparison: with a threshold of 70, kill 71 advances
the phase. Capturing the phase before credit is atomic with the counter update,
so the kill that enters Conflict retains its pre-Conflict honor behavior.

On first startup, open zones with kill thresholds begin in Tension with zero
kills. Open zones without thresholds begin their existing timed Conflict cycle.
Closed zones remain in Tension. Conflict advances to War, then Peace, then
Tension for zones with thresholds; timed-only zones return to Conflict. A zero
Peace duration retains the existing War-to-Conflict cycle.

`zone_conflict_states` stores the zone group, phase, cumulative kill count, and
nullable UTC deadline. It participates in the existing autosave and final-save
MySQL transaction, including when no characters are online. Startup restores
saved values after static templates load and advances overdue timed phases from
their original deadlines. Long outages skip complete timed cycles without
restarting their durations. Recovery uses the last committed save; an abrupt
process loss can lose changes since that save, like other autosaved world state.

Each open zone receives one repeating timer check during manager initialization.
State changes and broadcasts do not create timer chains. State, count, and
deadline are read together for saves and initial character packets; parallel
kills and timer callbacks share the same lock.

## Data and release impact

The application updater applies the additive
`2026-09-07_aaemu_game_zone_conflict_states.sql` migration before manager loading.
The base schema includes the same table. It creates a new table and does not
rewrite existing gameplay rows. Normal image publication and Keel delivery are
sufficient; no direct SQL or Kubernetes action is required.

No compact or client bytes change. The reviewed server compact SHA-256 is
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`:
16 conflict rows, 15 open, nine open with kill thresholds, and all `no_kill_min_*`
values zero. This change does not infer missing nonzero decay behavior or the
peace-protection policy tracked separately in #284.

## Validation

The focused unit suite covers all five threshold boundaries, full cycles,
zero-Peace cycles, restoration at every phase, elapsed phases and long outages,
parallel kills and timers, broadcast failures, closed zones, and timer ownership.
The existing disposable MySQL suite additionally checks migration reapplication,
round-trip counts/deadlines for every phase, upserts that clear deadlines,
transaction rollback, and commits with zero online characters.

After publication, confirm that hostile kills raise a land zone through the
escalation stages, Conflict/War/Peace follow their displayed deadlines, and a
normal server restart retains the saved phase/count/deadline. Gameplay and direct
cluster inspection require a later on-site check; automated tests do not claim
that human validation has happened.
