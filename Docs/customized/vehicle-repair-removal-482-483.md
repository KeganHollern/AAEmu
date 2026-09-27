# Vehicle repair and removal for r208022

This change resolves [aaemu-cluster#482](https://github.com/KeganHollern/aaemu-cluster/issues/482)
and [aaemu-cluster#483](https://github.com/KeganHollern/aaemu-cluster/issues/483).
The source base is `aed4e71f5d883562d44244d409693e13583d4bfa`.
No SQL schema, compact database, or client file changes are needed.
The server uses the current item details and `slaves` columns.

## Defects and resulting behavior

The old item reader lost every repair timestamp. The writer emitted 33 detail bytes for an active repair, although the client expects 29.
A destroyed item without a repair timestamp could pass the old summon check.
The server also checked repair state after object allocation and after removal of the previous vehicle.

The corrected reader preserves the destroyed flag, repair timestamp, and location bytes.
Both summon entry points reject destroyed or recovering items before those side effects.
A destroyed vehicle needs a valid repair. Elapsed time alone does not repair it.

The old repair effect accepted target IDs without proof that the caster owned the item in the caster's bag.
It ignored authored HP and MP values. An invalid effect could still consume the repair kit.
Repair now uses the normal skill transaction for the kit, item state, and saved vehicle state.

Manual removal now checks the current vehicle and its owner before it changes attachments or world state.
A rejected removal also stops a replacement summon. The server keeps internal cleanup separate from player removal requests.

## Exact-client evidence

The inspected client revision is `208022`. The research date is 2026-09-27.

| Input | SHA-256 |
| --- | --- |
| Original `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Original `xlcommon.dll` | `0e0881aa837553d7e307a5c6f9d886f3e82a0d72d94b6d666448aedaa94e2203` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |

The x2game preferred base is `0x38ff0000`. Its PE timestamp is `0x543cb835`.
The xlcommon preferred base is `0x33000000`. The addresses below use these bases.
The dump already existed. This task did not establish its original capture method.

### Fixed item body and time

`FUN_3961f500` calls `FUN_3961edf0` for item details at item offset `0x18`.
The latter reads `detailType = 2`, then copies exactly 29 raw bytes. The type byte is separate from that body.

| Body offset | Width | Server field or confirmed use |
| --- | --- | --- |
| 0 | 1 | `SlaveType` supplies the first byte of the combined native identifier. |
| 1 | 3 | `SlaveDbId` supplies the other identifier bytes through `WriteBc`. |
| 4 | 1 | `IsDestroyed` selects native destroyed error 98. |
| 5 | 8 | `RepairStartTime` stores Unix seconds. Zero means no repair timer. |
| 13 | 16 | The native summon gate uses these bytes for a stored-position condition. Their full encoding remains unknown. |

The native summon gate, `FUN_39349f00`, reads the timestamp directly at item offset `0x1e`.
In xlcommon, `XlGetCurrentFileTime` at `0x3301ca70` calls MSVCR100 `_time64` through IAT `0x3305d334`.
`XlDiffTime` at `0x3301ca80` forwards its 8-byte inputs to MSVCR100 `_difftime64` through IAT `0x3305d380`.
The timestamp uses Unix seconds, despite the `FileTime` name.

The gate compares elapsed seconds with double `600.0` at `0x399cfc38`.
The comparison at `0x3934a158` and branch at `0x3934a15c` permit the summon only when `600 < elapsed`.
At exactly 600 elapsed seconds, the client still rejects the summon and displays 0 seconds.
The server matches that whole-second rule. A summon can pass at 601 elapsed seconds.

The server preserves the final 16 bytes unchanged. It does not invent a stored-position format or add a new location rule.

### Authored repair items

The item descriptions below all state 10 minutes. Their skills target an item and consume 1 source item.
The items set `use_skill_as_reagent = true`. Their skills have no labor cost.

| Item | Name | Skill | Repair effect | HP | MP |
| --- | --- | --- | --- | --- | --- |
| 20061 | Shatigon's Sandglass: Spoonful | 17596 | 4 | 1000 | 1 |
| 23641 | Shatigon's Sandglass: Handful | 16561 | 2 | 33000 | 1 |
| 23642 | Shatigon's Sandglass: Pinch | 17595 | 3 | 1000 | 1 |
| 27411 | The English name is empty. Its description names the Comet Speedster. | 16561 | 2 | 33000 | 1 |
| 27412 | Shatigon's Toolbox | 21651 | 5 | 33000 | 1 |

`repairable_slaves` defines which effect can repair each vehicle template.
The researched server snapshot contains 29 original mappings and 1 custom mapping for vehicle template 555.
The server checks this table. An item name alone does not establish compatibility.

### Manual and automatic removal

`FUN_39349f00` calls `FUN_3935e3f0` with the active vehicle identifier.
The resolver searches the loaded client unit map for actor type 2 and that identifier.
A missing actor selects error 312, `SlaveDespawnNearTheSlave`.
The resolved vehicle flag at `+0x3bba` selects error 288, `SlaveCannotRemoveWhileInCombat`.
`FUN_3935c8d0` checks attached cargo before removal. The native path does not calculate a numeric removal distance.

Ordinary manual removal sends opcode `0x2f`, `CSDespawnSlave`, with the vehicle object ID.
Its serializer is `FUN_397c7600`, selected by vtable `0x399cfc54`.

`FUN_39349cb0` supplies a separate automatic path. It resets a timer while the actor exists in the client unit map.
It reduces the timer while the actor does not exist. At zero, it sends opcode `0x30`, `CSDestroySlave`, with the active identifier.
The initial timer, float `300.0`, is at `0x3999cfe0`.
The packet serializer is `FUN_397b94e0`, selected by vtable `0x399cfc60`. It names the field `tl`.

The manual function also has an alternate `0x30` producer controlled by vehicle flag `+0x3c26`.
This task did not prove that flag's full meaning. Dead vehicle removal remains the responsibility of internal corpse cleanup.

## Server enforcement and interpretation

A repair needs an authenticated source kit and its authored skill.
The target must be the caster's destroyed summon item in the caster's bag, with no trade reservation.
Its vehicle template must accept that repair effect. The saved vehicle row must match the item's ID, vehicle ID, template, and character owner type.

One `SkillLaborBatch` commits kit consumption, the repaired item state, and saved vehicle HP, MP, and ownership.
The transaction sets HP and MP to the authored repair values. This is the server interpretation of recovery after destruction.
The inspected client does not establish whether the original server added or set those values. No retail arithmetic claim is made.
The current summon path limits the restored HP and MP to the vehicle maximums.

The current bag owner supplies ownership. This permits a valid repair after a tradable destroyed summon item changes owner.
An invalid target, missing kit, rejected whitelist entry, repeated repair, missing vehicle row, or failed transaction consumes no kit.
The effect restores the item state if the transaction fails. It sends repair success only after commit.

A repaired item can coexist briefly with its old corpse. `Slave.Save` must not overwrite the committed repair values with that corpse's HP and MP.
The once-per-life death guard also prevents repeated death from erasing a repair or repeating death effects.

Manual removal and replacement need the owner's current root vehicle in the same world instance.
The vehicle must be living, visible, and present in the owner's server region neighborhood.
This uses the same neighborhood as actor visibility. It is an interpretation of the client actor-map condition, not a claimed retail metre limit.
Vehicle combat rejects removal. Owner combat alone does not. Attached cargo also rejects removal.
A failed save of a persistent vehicle rejects player removal before attachment or world changes.
This also stops a replacement summon. Internal cleanup keeps its current save policy.

Automatic removal needs the same owner and vehicle identity checks, no vehicle combat, and no cargo.
The server must observe continuous owner absence for at least 300 seconds. The vehicle must still be absent when the request arrives.
Visible entry resets the clock. Both individual and batched actor removal paths record absence.
The client request alone cannot establish that the time elapsed.

Logout, portal transfer, test cleanup, item cleanup, and corpse cleanup remain internal lifecycle paths.
They do not depend on player visibility or vehicle combat. Each caller keeps its own cargo policy.
Accepted removal clears attachment state for crew and child objects. Rejected removal leaves the current vehicle and its attachments intact.

## Automated checks

`VehicleRepairTests` covers destroyed-state rejection, strict time boundaries, the 29-byte body, timestamp reload, and preservation of unknown location bytes.
`SkillLaborVehicleRepairPersistenceTests` uses the real skill path, item reload, and MySQL transactions.
It covers authored points, kit consumption, failed commits, invalid targets and rows, traded ownership, repeated repair, and the old corpse save.

`SlaveRemovalTests` covers manual visibility, ownership, world identity, combat, cargo, replacement, internal cleanup, and continuous absence.
`SkillLaborTests.SlaveRemoval` checks replacement failure through the skill transaction.
Release evidence records the exact test counts and results. These automated checks do not establish the final client display or gameplay result.

## Focused human checks

Track results in [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
The release adds one comment with 8 checkboxes. Keep all earlier results in that issue unchanged.
Use a disposable test vehicle and record the summon item, vehicle template, repair kit, and observed result.
For the repair checks, prepare a matching kit and a kit outside the vehicle's repair whitelist.
Use the item table above and the authored whitelist. This document does not prescribe unconfirmed GM commands.

1. **Destroyed state, solo.** Destroy the vehicle, then try its summon item without repair. Confirm rejection before and after a relog.
   Confirm that elapsed time alone does not restore it. Keep a second active vehicle intact when a destroyed replacement is rejected.
2. **Repair and relog, solo.** Use 1 matching kit soon after destruction, while the corpse still exists if possible.
   Confirm that exactly 1 kit disappears. Relog during the wait and confirm that the timer continues.
   After more than 600 seconds, summon the vehicle. Check HP and MP against the authored values, limited by its maximums.
3. **Rejected repair, solo.** Try the wrong kit on a destroyed item. Try another repair during the wait and on a healthy item.
   Confirm that each rejection keeps the kit count and item state unchanged. Confirm that a missing kit cannot repair the vehicle.
4. **Nearby removal, solo.** Remove a nearby vehicle outside combat. Confirm that the vehicle and its attached objects disappear normally.
   Summon it again and confirm that ordinary movement and vehicle controls work.
5. **Distant removal and replacement, solo.** Leave the vehicle's actor visibility area. Before 300 seconds, try manual removal and another vehicle summon.
   Confirm rejection and no replacement. Return and confirm that the original vehicle remains usable.
6. **Combat and replacement, solo.** Put the vehicle in combat. Try removal and another vehicle summon.
   Confirm that both fail and preserve the vehicle. Leave combat and confirm that nearby removal works.
7. **Automatic removal and timer reset, solo.** Leave actor visibility, then return before 300 seconds. Leave again and remain absent.
   Confirm that automatic removal uses the new absence period. After that removal, confirm that the summon item works again.
8. **Cargo and crew, 2 characters.** Attach cargo and try nearby removal. Confirm rejection and intact cargo.
   Remove the cargo, board both characters, and remove the vehicle. Confirm that both characters detach and can move normally.

Human gameplay validation remains pending until these results are recorded.
