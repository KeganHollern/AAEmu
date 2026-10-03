# Mate injury and recovery in r208022

This change addresses [aaemu-cluster #489](https://github.com/KeganHollern/aaemu-cluster/issues/489) through the client's living injury state.
The original data does not establish the ticket's proposed recovery timer.
Lethal damage leaves a persistent pet downed at 1 HP with an injury that needs treatment.
Temporary skill summons keep their ordinary death path.

This change also adds `MateMakeGetUp` and `HealPet` from [#323](https://github.com/KeganHollern/aaemu-cluster/issues/323).
Issue #323 remains open for its other effects.
Stablemaster repair remains separate under [#315](https://github.com/KeganHollern/aaemu-cluster/issues/315).
The recovery potion supplies treatment in this batch.
No SQL schema, compact database, or client file changes form part of this change.

## Input identity and evidence

The installed client history identifies revision `208022`.
The native check reused the local runtime dump. It did not capture a new live session.
Both native images have PE timestamp `0x543cb835`, image base `0x38ff0000`, and entry RVA `0x8c5d6d`.

| Input | SHA-256 |
| --- | --- |
| Original `bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Original stock client compact | `784d362434a2a0fd0a29fbc1bbb7f771d9ffbe2c8568339ac54d6fdcf44d2ed7` |
| Deployed server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The stock compact came from the original local r208022 client archive.
The selected recovery rows match the deployed server compact.
The ignored cluster directory `.tools-re/mount-recovery-489-323-20261003/` holds the bounded research record.
This document retains facts and addresses, not client assets or raw disassembly.

| Contract | Native evidence |
| --- | --- |
| Mate item detail | `0x3961edf0` reads detail type 3 as 6 payload bytes. `0x3961f8f0` establishes the item detail offset. |
| Repair flag | `0x39423560` exports item byte `+0x1d` as boolean `dead`. `0x39447a40` uses that byte for the pet repair quote. |
| Get Up action | `0x393bbc90` offers skill 13719 for an owned Mate with buff 1579. `0x3933b1b0` checks ownership. |
| Get Up target | `0x39365230` passes the Mate object ID to `0x3935ac40`. The latter sends a Character caster and explicit Mate unit target. |
| Pet command mode | `0x3906aea0` registers Stand, Passive, Protective, and Aggressive modes as `UserState` values 0, 1, 2, and 3. |
| Spawn delay | `0x3989a910` reads `spawnDelayTime`. `0x3933ac10` adds it to a local timer, but this does not establish a death penalty. |

Native dispatcher `0x393be280` returns immediately after the special Get Up handler sends the skill.
Get Up therefore bypasses the later generic interaction path and does not need a different `CSStartInteractionPacket` response.

The 600000 ms repair display in `0x3944d9e0` and `0x394e2910` applies to ship item kind 8.
It does not apply to Mate item kind 11.
The bounded check did not establish the original server's resummon posture or a pet recovery countdown.

## Recovery rules

Buff 1578 represents injury. Its duration is 0, and its movement modifier is -300, which reduces movement by 30 percent.
Its text says the pet stays at 1 HP until treatment.
Buff 1579 represents the downed state. Its duration is also 0, and it supplies ragdoll, stun, and damage immunity flags.
Both buffs apply to living units.

| Action | Authored data | Result |
| --- | --- | --- |
| Get Up | Skill 13719, `MateMakeGetUp` row 2215, 5000 ms cast, 4 m range. | The pet stands. Injury, 1 HP, and reduced movement remain. |
| Recovery potion | Item 18649, skill 15220, `HealPet` row 3461, values 20/20/0/0, 2000 ms cast, 3 m range. | Injury and the downed state clear. The pet gains 20 percent of maximum HP and MP, capped at each maximum. |

The potion uses the current integer percentage calculation and consumes 1 source item after a successful recovery transaction.
Its authored cooldown is 5000 ms.
Requirement 37 permits potion skill 15220 while the downed buff tag 371 is active.

Get Up retains the explicit Mate target for ownership, range, and its effect.
Its compact target type remains `Self`.
The server therefore uses the Character as the subject of its target buff requirements.
This preserves the original Self requirement subject and avoids a false rejection from requirement 37.

The server limits both recovery actions to the owner's active persistent pet in the same world instance.
The pet's exact summon item must remain in the owner's bag and outside a trade reservation.
Get Up needs the downed state. Recovery does not accept temporary summons or another character's pet.
Completion checks the exact Mate object again, so a reused object ID cannot redirect an old cast.

The user selected standing resummon at 1 HP until treatment.
This is an explicit server rule, not a confirmed original-server rule.
Dismissal and reconnect preserve injury through the summon item's repair flag.
The 6-byte payload remains experience, repair flag, and level. Healthy uses 0, and injured uses 1.
No separate downed flag or timer is added to that payload.

## Transaction and test scope

Get Up and potion recovery use the current skill transaction.
The potion's item cost and the recovery state commit together.
A failed transaction restores the pet state and the source item count.
Buff updates and item publication follow a known commit.
The mount transition uses the persistence lock before attachment locks, so it cannot attach a rider during an uncommitted recovery.

The content tests check the original skills, effects, requirements, buffs, and item data.
The runtime tests exercise `ApplyEffects`, source authority, stale targets, range, repeated callbacks, consumption, and failed commits.
Persistence tests check the item detail and active Mate state across a database save and load.
The focused concurrent check must show that a mount request waits for recovery and cannot bypass a failed transaction.
Test results belong in the release evidence. This document does not claim a completed test run or human validation.

## Pending human validation

These checks belong in the high-priority [HUMAN VALIDATION issue #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
They remain pending until the user records their results.

1. Let an owned mount take lethal damage with a rider. Check the downed pose, detached rider, 1 HP, and blocked mount entry.
2. Use Get Up within 4 m. Check the 5-second cast, standing pose, 1 HP, and reduced movement.
3. Let the injured pet take another hit. Check that it falls again, then use the potion while it is downed.
4. Check the 2-second potion cast, removal of both states, 20 percent HP/MP restoration, and consumption of exactly 1 potion.
5. Dismiss and resummon an untreated pet, then reconnect. Check that it stands at 1 HP and retains injury until treatment.
6. Cancel a recovery cast or move outside its range. Check that the pet keeps its state and the potion count does not decrease.
