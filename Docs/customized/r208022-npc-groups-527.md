# NPC groups for r208022

This change resolves the general spawn and lifecycle work in cluster issue #527.
It adds no encounter scripts, dungeon placements, compact rows, or packet fields.

## Evidence

The source base is `d60b70d0984456c634c45dd32ac4ea975ac1808a`.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The client compact SHA-256 is `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
The client `x2game.dll` SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.

The server compact contains 158 groups and 571 member rows.
All member NPC IDs resolve to NPC templates.
The group tables do not exist in the client compact.
Native function `FUN_396d16f0` loads the group fields and member fields.
Function `FUN_396d19a0` recognizes the `NpcGroup` spawner target type.
These loaders do not prove the server runtime rules below.

Commit `419ed0cd3faa935d6ff2fdc98e52539a360e5431` added the data models and loaders.
It left the group runtime for later work.
The old `SpawnNpcGroup` method passed the group ID to the ordinary NPC spawn path.
That path treated the group ID as an NPC template ID.

Group `50` provides a concrete example:

| Member row | NPC template | Leader | Move leader | Offset | Tension |
| --- | --- | --- | --- | --- | --- |
| 191 | 8564 | Yes | Yes | `(0, 0, 0)` | 2 |
| 192 | 8566 | No | No | `(2, -3, 0)` | 1 |
| 193 | 8566 | No | No | `(-2, -3, 0)` | 1 |

Spawner `9571` permits 1 occurrence of this 3-member group.
Its authored delay is 125 seconds.
Group `50` sets `enable_respawn=false`, but its outer spawners remain active and have positive delays.
So that flag cannot safely suppress every future group occurrence.
Group `187` has no move leader. The runtime accepts this authored case.

## Group creation and placement

Each spawn creates a separate `NpcGroupInstance`.
The runtime identifies a member by its member row ID.
Two members can use the same NPC template without replacement of either member.
The runtime prepares all members and attaches them to the group before publication.
It publishes every member before any member's spawn event runs.
It also synchronizes group combat after publication and before spawn events.
A failed group creation removes the prepared occurrence.

Explicit spawner IDs keep their authored `NpcGroup` rows, delays, and population limits.
The normal pinned-spawner activation rules remain in use.
A pinned spawner enters the periodic world update after `Activate()`.
An explicit script can also use `ForceSpawn()`.
An explicit spawn can replace an empty suppressed occurrence after all corpses leave.
The periodic world update cannot use that path to repopulate an instance.
This change does not activate new content by itself.

The automatic NPC index contains only ordinary `Npc` rows.
Captured individual NPC placements remain individual placements.
The runtime does not infer a whole group from a shared NPC template ID.
This avoids duplicate groups at captured mother and baby positions.

The initial formation rotates authored offsets with the spawner's local yaw.
This rotation frame is an explicit server interpretation, not a recovered retail rule.
The change preserves the current terrain correction and authored-height rules.

## Formation movement

The exact client file `game/scripts/ai/goalpipes/pipe_manager_x2.lua` defines `Create_formation_GoToOnPoint`.
Its `IF_LASTOP_DIST_LESS` operation compares the formation distance with `formationTension`.
So tension is a distance threshold, not an offset multiplier.
The client HoldPosition and Roaming scripts use this formation operation for group followers.

Idle HoldPosition and Roaming followers use the move leader's position and yaw.
Each follower keeps its authored offset relative to that leader.
A follower stops when its distance is below its authored tension.
Combat keeps the normal combat movement rules.

The available client evidence does not establish a replacement rule for a dead move leader.
This server does not select a replacement leader.
Surviving followers hold their current positions when the move leader dies or disappears.
Normal member refill or whole-group replacement can restore the authored move leader.

## Approved server respawn rules

The user approved these rules because the exact client does not prove the partial-group respawn contract.

- With `enable_respawn=false`, survivors remain without missing members.
- With `enable_respawn=true`, missing member rows return after the selected outer spawner delay.
- A member cannot return before its previous corpse leaves the world.
- A full wipe replaces the whole occurrence after the last death's delay and corpse removal.
- Each occurrence keeps its selected delay. A later spawn cannot change that timer.
- Population limits count occurrences. They do not count each physical group member as a separate occurrence.
- Closed schedules, explicit resets, instance-world suppression, and event ownership retain their current limits.

An explicit spawn effect suppresses automatic respawn for its whole group occurrence.
Its lifetime and inherited target rules apply to every group member.
Other occurrences keep their own selected positive delays.

Member removal clears its runtime membership.
Group retirement clears all member references and shared combat references.
It also clears each former member's local threat and target subscriptions.
An explicit spawn can replace an empty suppressed group without an automatic instance respawn.
A skill-driven group spawn applies its lifetime and target rules to every member.
It also clears each former member's local threat and event subscriptions before queued removal.
World teardown deactivates each spawner and cancels its pending group timers.
Group templates hold no live NPC references.
Compact reload replaces the data snapshot without changing templates held by live occurrences.
No SQL or persistent group state is needed.

## Checks

`NpcGroupSpawnTests` covers authored membership, duplicate NPC templates, occurrence isolation, and publication order.
It also covers partial refill, full-wipe delays, corpse removal, reset, schedule expiry, and respawn suppression.
Other cases cover explicit placement, random spawn tracking, data reload, and formation rotation.
The exact-compact test reads group `50` when `AAEMU_COMBAT_TEST_COMPACT` names the server compact.
The combat lifecycle tests cover removal from a live group through the real NPC deletion path.

Human validation still needs an explicit outdoor group placement on the deployed server.
Check that 1 mother and 2 babies appear for group `50`.
Check idle formation, death of one follower, full-group replacement, and loss of the move leader.
Use a separate group with `enable_respawn=true` to check partial member refill.
Do not infer that a captured individual spawn belongs to a runtime group.
