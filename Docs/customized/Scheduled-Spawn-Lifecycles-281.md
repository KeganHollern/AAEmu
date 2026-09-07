# Scheduled NPC and doodad lifecycles

Resolves [aaemu-cluster #281](https://github.com/KeganHollern/aaemu-cluster/issues/281).
Reviewed starting source: `ff4a243a61a98e785f92e651f7bef7f1d746ae38`.

NPC spawning and schedule expiry use one active-window decision. Any active
associated schedule keeps the occurrence active; an inactive or ended schedule
prevents spawning and retires the occurrence, including NPCs in combat. Entries
without a schedule use their ordinary time window. Direct, task, event, effect,
and explicit NPC spawn entry points observe the same schedule decision.

A scheduled doodad is allocated when its window opens. Each subsequent window
creates a fresh occurrence and object ID, with its initial phase state. Spawners
own retirement and ID release for both direct and queued removal. Old despawn,
respawn, and phase callbacks cannot retire, release, or change the replacement.
NPC pending respawns belong to their original occurrence; window expiry,
explicit scheduling resets, and cloning do not carry that ownership into a new
window or another spawner. Ordinary doodad final-phase respawns retain their
existing two-stage behavior.

## Reference data

Read-only inspection of the server compact with SHA-256
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` found 174 NPC
schedule associations covering 118 spawners and 119 doodad associations covering
108 templates. One NPC association and 15 doodad associations reference missing
schedule definitions. The existing schedule manager treats those entries as
inactive; the spawning paths now consistently follow that decision.

Most linked doodad schedules contain historical 2013/2014 date bounds. The
remaining doodad entries without date bounds are all-day schedules. Deterministic
clock-driven tests establish two-cycle recurrence without editing those data
rows. NPC spawner 15776 is a current recurring example with schedules 63–68,
covering two-hour windows every four hours.

This release changes no compact, client data, protocol, or SQL for #281. The
server fix is independent of client-file research.

## Validation

Regression tests exercise two full recurrences, overlapping windows, the full
NPC schedule-status/combat matrix, real fresh doodad allocation, and ordinary
Final-task respawns. They also dispatch stale removal and respawn work through
the production SpawnManager path and verify object-ID ownership, phase-task
ownership, explicit resets, and independent cloned-spawner state.

After publication, check an active recurring NPC spawn through its end and next
start, including an NPC in combat at expiry. Confirm repeated dungeon/event
spawns do not duplicate objects or leave expired effects active. These focused
in-game checks still require human validation; automated tests establish the
lifecycle behavior without running the game client.
