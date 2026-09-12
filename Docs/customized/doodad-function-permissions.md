# Housing and doodad permissions for r208022

This change resolves aaemu-cluster issues #493, #494, and #495.

## Exact client evidence

The client history reports revision `208022`. The source DLL and runtime dump
have the same PE timestamp `543cb835`, image base `38ff0000`, entry point
`008c5d6d`, and image size `01cb0a00`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

The dump is the same local research input used by earlier r208022 work. This
change does not create a new dump or modify client content. Ghidra exports stay
under `.tools-re/housing-20260912/permissions` in the cluster workspace.

The exact-client evidence confirms these rules:

- The SQL loader at `39673b90` reads `doodad_funcs.perm_id` from column 8.
  Each function record has a stride of `0x2c`. Permission is at offset `0x20`.
- `393bc260` selects the requested function and passes its permission to
  `393b3300` before its function-specific effects.
- `394f9bc0` checks every function in the current group before the craft window
  permits use. It passes the same offset `0x20` to `393b3300`.
- `393b3300` contains the permission switch from 0 through 8. Its default
  branch denies access.

| Value | Native rule | Evidence |
| --- | --- | --- |
| 0 | Any caster | `393b3300`, case 0 |
| 1 | Owner character | `393b3300`, case 1 |
| 2 | Owner or the owner's family member | `393b3300`, case 2, and family lookup `39302aa0` |
| 3 | Guild leader or a role with `SiegeMaster`, with a matching doodad guild when one is set | `392f8af0` and role serializer `397bd570`, offset `0x90` |
| 4 | Owner or the owner's party, including the same group of 5 within a raid | `393b3280`, `39308e10`, and `39308e40` |
| 5 | Owner or any member of the owner's party or raid | `39308dd0` scans the 50 team slots |
| 6 | Owner or the same account | `393b32c0`, house account at offset `0xa8` |
| 7 | Member of the player nation that owns the zone Dominion | `39053360`, `392fa6b0`, and `392f8180` |
| 8 | Account with a house in the zone group | `3932aaf0` and zone-group resolver `397a6c60` |

The native owner resolver uses the parent object's owner when the doodad has
an active parent. The server uses the current house or Slave owner for the
same reason. Name, family, and guild managers retain offline character data.

The current `Type2` field carries the guild identifier for rule 3. Serializer
`3983b8b0` writes it at native record offset `0x5c`. Initializer `393a5930`
copies it to runtime doodad offset `0x350`. Rule 3 reads that field. The
server keeps the current wire field order and does not add a packet field.

Both compact snapshots contain 746 nonzero permission rows. Their counts are
656, 1, 1, 2, 6, 11, 7, and 62 for values 1 through 8.

## Server behavior

`DoodadPermissionRules` checks the selected function before `Doodad.Use`,
`Doodad.DoFunc`, or the direct recovery path changes the object. A denied
function sends `InteractionPermissionDeny`. It does not run phase functions
or produce theft evidence. A paid skill denial also fails the current labor
batch, so earlier effects and labor return to their previous state.

Crafting checks all current function permissions at the start and again
before it grants products. A permission change during the cast cancels the
craft before materials or products change.

`House.AllowedToInteract` grants access to the current owner's account for
Private, Family, Guild, and Public land. Family and Guild then compare the
visitor's membership with the owner's stored membership. No membership,
missing owner membership, and invalid permission values deny access.
Unfinished houses and templates with `AlwaysPublic` retain public access.
The owner cannot select Family or Guild without the matching membership.

`DoodadFuncRecoverItem.TryRecover` checks house access before both the stored
item path and the item-template path. It returns success only after the item
transfer succeeds. `RecoverItem.Execute` deletes the doodad only after that
success. A denied action leaves the persistent doodad and attached item intact.
The labor batch records the old doodad state before recovery clears its item
references. A known commit failure restores both the item and those references.

## Scope limit

Permission 7 denies access while the server supports only unclaimed Dominion
state. No nation owns a territory in that state. Issue #143 retains the full
Dominion, nation, and siege lifecycle. This change does not add a claim path,
create a nation, or grant access through a guessed faction relationship.

The native enum and consumers are confirmed. No human gameplay test occurred
for this change.

## Checks

The Release build passed. The full unit suite passed all 3,284 tests with 0
skips. The run used both the server compact and the exact-client housing
fixture. No SQL schema changed.

Tests cover each permission value, unknown values, offline owner accounts and
families, house ownership changes, wrong guilds, missing membership, guild
role authority, raid subgroups, resident zone groups, and both recovery paths.
They also cover a denied function with phase effects, a permission change
during crafting, a failed labor batch, and recovery after a known SQL failure.

Use the published build for these focused client checks:

1. Set a house to Family or Guild and try it with an unrelated character.
2. Use a character on the owner's account while the owner is offline.
3. Try to recover private furniture and a private trade pack without access.
4. Check that each denied object and its attached item remain in place.
5. Try a baby mount, a raid pack chest, and a resident workbench with valid and invalid access.
6. Change access during a craft and check that the denied craft gives no product or labor charge.
