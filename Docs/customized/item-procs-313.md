# Item procs for r208022

This change resolves the authored item-proc paths in [aaemu-cluster#313](https://github.com/KeganHollern/aaemu-cluster/issues/313).
The source base is `474c2436276b352ca9edb5cada47b2cfd80940e8`.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
No SQL schema, persistent item record, compact, client binary, or packet layout changes.

## Defects and sources

A new proc never passed its cooldown check. Forced rolls also failed, and a zero-percent roll could succeed.
Only 2 damage chance kinds ran. Direct bindings and holdable procs did not attach.
Repeated set updates could add duplicate procs. The last set piece could leave a proc or set buff active.
The old cast used the owner as a doodad target for every proc.

The compact defines 99 procs, 51 direct bindings, 22 holdable references, and 37 set-proc bonus rows.
All proc skill references resolve. Thirteen direct bindings reference absent item templates.
Those bindings remain inert because no current item can supply them. The loader preserves all binding rows and deduplicates repeated item/proc pairs.

Sources now include equipped items, their holdables, their applied `RuneId` lunafrost, and qualifying equipment sets.
The client confirms 5 lunafrost sources, items 26855 through 26859, with procs 72 through 76.
A broken item supplies no proc. Equipment removal, repair, replacement, and login refresh source membership.
One source can leave without removing a proc that another source still supplies.
Set buffs and set procs each update independently, including when the last set piece leaves equipment.

## Exact-client evidence

The client reports revision 208022. The inspected x2game dump SHA-256 is
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The original binary SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
Both have image base `0x38ff0000`, image size `0x01cb0a00`, and PE timestamp `2014-10-14 00:44:21`.

The native loader at `0x396c9e10` reads the chance kind, chance parameter, chance rate, cooldown,
finisher flag, item-level modifier, and skill ID. It has no per-second chance field.
The binding loader at `0x396ca060` resolves proc references.
The tooltip at `0x39421e20` resolves the applied enchanting gem at native item-detail offset +8.
This matches `RuneId` at wire-body offset +7, after the one-byte native detail discriminator.
The socket array is separate, at native +0x18 and wire +23. No current socket gem supplies a proc.

Proc 46 is the only finisher. Its description specifies a critical-chance buff after an enemy kill.
The server dispatches this proc after a killing blow. Its set sources are the 4-piece bonuses of sets 125 and 133.
The proc skill target types are Self (80), Hostile (17), and AnyUnit (2).
The cast uses the owner for Self and the event counterpart for the other authored target kinds.
The normal skill path still checks the target and cast requirements.

## Explicit server rules

The client exposes the level modifier but does not expose its activation calculation.
Kegan approved this custom rule on 2026-09-27:

```text
chancePercent = clamp(baseChance + itemLevel * levelBonus / 100, 0, 100)
```

For example, level-50 proc 28 has a 6% chance: 3 + 50 * 6 / 100.
Level-50 proc 29 has a 15.5% chance: 8 + 50 * 15 / 100.
These are server rules, not claims about the original retail calculation.

Kegan also approved 1 roll and 1 shared cooldown per proc ID.
The server uses the highest item level among the active sources of that proc.
Equipment refresh, unequip, and re-equip preserve its cooldown until logout.
A failed roll or rejected cast does not start the cooldown. A forced roll bypasses chance, but not cooldown or the finisher condition.

Successful damage dispatch uses the authored `fire_proc` flag and the actual damage type and critical result.
This covers all 11 chance kinds in the current compact. Corresponding siege and heal kinds also use their server events.
Avoided, immune, and zero-value damage do not activate damage procs.
An automatic proc does not replace the player's active cast or plot wait.
Proc-origin effects do not start another item proc. That origin follows delayed buffs and chained skills.
The source equipment level also supplies the proc skill and buff level.

`FireSkill` (19) and `HitSkill` (20) remain unsupported. No current proc uses either kind.
Their exact event timing remains unknown. This release does not assign meanings from enum names alone.

## Validation

Deterministic tests cover chance boundaries, cooldown boundaries, failed casts, forced rolls, finisher conditions,
source membership, equipment swaps, complete set removal, lunafrost changes, broken equipment, duplicate sources,
damage-type and critical dispatch, target selection, active-cast preservation, and delayed proc recurrence.
Release evidence records the exact build and test results.

Pending human checks belong in HUMAN VALIDATION #573:

1. Equip a proc weapon and check its self buff during ordinary attacks.
2. Check a hostile-target proc on the attacked unit.
3. Check a lunafrost proc before and after equipment removal.
4. Check a set proc, then remove its final qualifying piece.
5. Trigger a cooldown, remove and re-equip the item, and check that the cooldown remains active.
6. Check a defensive proc during a spell cast. The original cast must continue.
7. Check proc 46 after a kill and after a nonlethal hit.

These checks do not validate dungeon encounter rules.
