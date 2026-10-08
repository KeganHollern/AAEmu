# Coffer permissions, issue 649

## Result

Issue [649](https://github.com/KeganHollern/aaemu-cluster/issues/649) identified a
trace-only `DoodadFuncCofferPerm`. The permission change already uses a separate
packet. It does not need a second permission store or a skill-side change.

The review found 2 related defects. `ChangeDoodadData` accepted values outside
the 4 client options. `DoodadCoffer.AllowedToInteract` converted the stored
32-bit value to a byte enum and granted access for unknown values. For example,
`258` became Public (`2`). The fix rejects invalid changes and denies access for
invalid stored values without byte conversion.

The unused `CofferContainer.CofferPermission` property was removed. It had no
readers, writers, or persistence path. `Doodad.Data` remains the single permission
value. The skill function now states its purpose and prevents a phase change.

## Exact client and data

The client identifies itself as `r208022` in `client/history.txt`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Decoded `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `game/scriptsbin/x2ui/inventory/coffer_permission.alb` | `2a429de17b2ca5df46725e4d91c63f22b1a402dfe600186269b8516040b85844` |

The source DLL and the decoded DLL share PE timestamp `0x543cb835`, image base
`0x38ff0000`, entry point `0x008c5d6d`, and image size `0x01cb0a00`.
The review reused the project's decoded research image. It did not make a new
runtime capture. A new read-only extraction from `client/game_pak` matched the
previous coffer UI extraction exactly.

Both compacts contain 5 `doodad_func_coffer_perms` rows with only an ID. Their
function rows use skill `22691`, owner permission `1`, and `next_phase=-1`:

| Function row | Function group | Permission descriptor |
| --- | --- | --- |
| 14412 | 17762 | 1 |
| 14745 | 5602 | 2 |
| 14746 | 5653 | 3 |
| 14747 | 5918 | 4 |
| 15106 | 18659 | 5 |

No descriptor contains a selected permission. The 5 rows are not 5 permission
values.

## Confirmed client path

1. Descriptor loader `39673b90` maps `DoodadFuncCofferPerm` to action type
   `0x51`. Local action dispatcher `393bc260`, case `0x51`, stores the selected
   object, emits UI event `0x243`, and returns without a network packet.
   Event dispatcher `3920fa60` uses name table `39d22ce8`; entry `0x243` is
   `39d235f4`, registered as `COFFER_INTERACTION_START` at `3921656d`.
2. `coffer_permission.alb` receives `COFFER_INTERACTION_START` and checks
   `X2Coffer:IsMyHouseCoffer()` before it shows the permission window.
3. The Apply callback calls `X2Coffer:SetHouseCofferPermission` with the selected
   value. Its UI map is `{1, 4, 2, 3}`.
4. Native registration at `3945fc38` binds `SetHouseCofferPermission` to
   `3945cb90` through the Lua wrapper `3945ea60`.
5. `3945cb90` subtracts 1 from the Lua value and calls `393b36a0` with the selected
   coffer object. The resulting values are Private `0`, Family `3`, Guild `1`,
   and Public `2`.
6. `393b36a0` checks the target, ownership, and the family or guild requirement.
   It sends packet `0xEB` only when the selected value differs from the current
   value. `393a1200` reads that current value from doodad offset `0x3a8`.
7. The packet constructor `397ac8f0` stores the object ID at `+0x0c` and the
   selected value at `+0x10`. Vtable `399d2dc4` selects serializer `397bbc40`.
8. `397bbc40` writes the 3-byte object ID, then the 32-bit value. It writes no
   optional fields or trailing groups.

The native producer, serializer, Lua callback, and current Game reader agree:

| Body offset | Type | Value |
| --- | --- | --- |
| 0 | unsigned 24-bit, little endian | Doodad object ID |
| 3 | signed 32-bit, little endian | Permission `0` through `3` |

The body is 7 bytes. `CSChangeDoodadDataPacket` is C2G, level `1`, opcode `0xEB`.
The current level and registration are unchanged. This fix does not alter the
wire format or add a client patch.

## Current server path and persistence

The deployed source basis is `70079df5376511cbf769a217409b48d0770693c2`.
The original coffer implementation `f10a1ea55` already used
`ChangeDoodadData` for permission changes.

`CSChangeDoodadDataPacket` resolves the doodad in the active character's world.
`DoodadManager.ChangeDoodadData` checks the character owner and the selected
family or guild requirement under `SaveManager.PersistenceSyncRoot`.
It assigns `Doodad.Data` and broadcasts `SCDoodadChangedPacket` (`0x10E`).
The response contains the same 3-byte object ID and 32-bit value.

The `Data` setter calls `Save()` for a changed persistent doodad.
`WriteDoodad` writes the value to `doodads.data`.
`SpawnManager.SpawnPersistentDoodads` reads the integer and calls `SetData` during
reload. The ordinary doodad description also sends `Data` on client load.
This path needs no new SQL column or migration.

`OpenCofferDoodad` checks `AllowedToInteract` before it assigns the opener.
Both inventory transfer operations also call `CanUseCoffer`, which checks the
current permission. A visitor who keeps the window open loses transfer access
when the owner changes Public to Private. Family and guild checks consult
current membership, including the stored membership of an offline owner.

This change keeps the current rules for valid values. A normal Private coffer
permits the owner's account. A Private Otherworldly coffer permits only the
owner character. The permission dialog and permission packet remain limited
to the owner character.

## Automated and manual checks

`CofferPermissionTests` covers all 4 valid values, exact request and response
bodies, every truncated request length, unknown objects, owner authority,
missing membership, and invalid values including byte-wrap cases.
It also covers access after a Public-to-Private change, offline membership,
normal Private coffers, Otherworldly coffers, and the local permission function.
The release record contains the final build and test results.

The code trace confirms the persistence path. This review did not write to a
live database or claim a manual client pass.

The manual checks need an owned coffer and the r208022 client:

- Change between Private and Public. Reopen the window and check the selected
  value. Reconnect and check it again.
- If the owner has a family or guild, select that permission and check its
  selected value after reconnect.
- Use another account to open a Public coffer. Change it to Private while that
  visitor keeps the window open. The visitor must not move or split its items.
- Check that a family or guild visitor loses access after removal from that
  family or guild.
- After a normal Game restart, check that the selected permission remains.

Record these results in the single HUMAN VALIDATION issue `573`.
