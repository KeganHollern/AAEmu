# Doodad ratio selection: #221

This record covers [#221](https://github.com/KeganHollern/aaemu-cluster/issues/221).
The source baseline is `4d0f9ff2cdd6bbd0e01e07305ac6879820aaff81`.
The source baseline uses separate random rolls and inconsistent cumulative state.
The reviewed client does not prove the retail selection algorithm.
The correction uses the server rule that Kegan approved on 2026-10-08 UTC.
Retail research continues in [#680](https://github.com/KeganHollern/aaemu-cluster/issues/680).

## Inputs

The client history states revision `208022`.
The source DLL and the pre-existing dump share image base `38ff0000`,
PE timestamp `543cb835`, entry point `008c5d6d`, and image size `01cb0a00`.
The original runtime dump capture method is unknown.

| Input | SHA-256 |
| --- | --- |
| Source `bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

## Source baseline and confirmed client behavior

`DoodadManager` loads phase functions in `doodad_func_group_id, actual_func_id`
order. `Doodad.DoPhaseFuncs` selects a new integer from 0 through 9,999 before
each function. `DoodadFuncRatioChange` tests that value plus
`CumulativePhaseRatio`, but does not advance the cumulative value after a miss.
`DoodadFuncRatioRespawn` ignores the cumulative value in its comparison, then
subtracts the row weight after a miss. The phase walk does not reset that state.
Both comparisons use `<=`, which includes one extra outcome at each boundary.

Native loader `3966d670` reads `id, next_phase, ratio` from
`doodad_func_ratio_changes`. Loader `3966d810` reads `id, ratio, spawn_doodad_id`
from `doodad_func_ratio_respawns`. Phase loader `39674d30` resolves these
descriptors. Phase handler `393a6c60` applies the phase that the server supplied.
The inspected paths do not select a random branch.
The data establishes field meanings, but not normalization or overflow rules.

The Git history also does not prove retail behavior. Upstream commit
`9091b03cdcc36bf50b8807ac90a2e105131aa815`, PR #578, moved the roll inside the
loop to fix quest 1135. It disabled the previous accumulator update.
Its report describes the quest result, not a general probability contract.
Commit `1d2530e73df832e729a8d2572eb7de90df28a053` later subtracted respawn weights
without using the cumulative value in that function's comparison.
Neither change is evidence for the original server's full algorithm.

## Confirmed compact inventory

These counts use distinct phase groups, not repeated template joins.
The original issue's aggregate counts do not describe this inventory.

| Function family | All referenced groups | Groups with a current group record | Current group totals below / equal to / above 10,000 |
| --- | ---: | ---: | --- |
| `DoodadFuncRatioChange` | 445 | 422 | 5 / 408 / 9 |
| `DoodadFuncRatioRespawn` | 161 | 143 | 7 / 131 / 5 |

No group mixes the 2 ratio families. No ratio definition is zero or negative.
Some groups refer to absent templates or inactive content.
A current group record alone does not prove a live spawn path.

Weather group 7055 contains 5 respawn weights of 2,000.
Independent comparisons bias the earlier branches and leave a no-selection
outcome, although the authored weights total 10,000.
This strongly supports a shared weighted selection as an inference.

The data also has totals below and above 10,000. Examples include:

- Group 7286 has 3 change weights of 3,300 and a timer.
- Group 3058 has 12 respawn weights with a total of 9,998.
- Group 2372 has change weights 2,500, 2,500, 2,500, and 10,000.
- Group 9233 has 3 change weights of 5,000.
- Group 8987 has 2 respawn weights of 50,000.

Overflow rules have a real content effect. Group 9233 belongs to template 3754,
which has 8 tracked world spawns. A fixed 10,000 range with ordered clipping
makes its third branch unreachable. Normalizing the whole group instead also
changes authored odds. Neither rule follows from the client loader.
The approved correction includes this overflow behavior.
Issue #680 records the affected live branches for later retail research.

## Approved server rule

Each visited phase with functions gets 1 integer roll from 0 through 9,999.
Its cumulative weight starts at 0. Ratio rows use the loader's current order.
Each row gets the half-open interval from the previous total to the new total.
The new total cannot exceed 10,000. Zero or negative weights get no outcomes.
Functions outside the 2 ratio families do not consume weight or change the roll.

For example, 5 weights of 2,000 each get exactly 20% of the outcomes.
Weights of 3,300, 3,300, and 3,300 leave 1% for no selection.
Weights of 8,000 and 8,000 get 80% and 20%.
Weights of 5,000, 5,000, and 5,000 get 50%, 50%, and 0%.
This clipping policy can make a later destination unreachable.
It does not normalize the authored weights or claim a retail match.

`RatioChange` selects a destination phase. That phase gets a new roll and total.
`RatioRespawn` keeps the normal spawner and target-template checks.
An invalid selected target consumes its interval without a replacement selection.
When no row succeeds, later non-ratio functions still run.
The normal phase cycle guard and function stop results still apply.

A selected self-transition clears its consumed override.
Previously, that value could replace the next interaction's authored destination.
Template 3094 demonstrates the defect. Ratio row 170 selects its current phase,
7079, but interaction 6385 must still advance to phase 7080 afterward.

Labor transactions capture both the roll and cumulative weight.
A failed transaction restores both values with the previous phase.
The normal transaction path defers a selected replacement until commit.

## Validation

Tests cover exact interval boundaries, one shared roll per phase, fresh rolls
after phase transitions, no selection below the full range, and overflow clipping.
Full enumeration checks all 10,000 integer outcomes without a statistical test.
Phase tests cover interleaved functions and later non-ratio actions.
Respawn and labor tests check target guards, deferred replacement, and failed
transaction restoration.

Human weather and gathering checks belong in HUMAN VALIDATION #573 after a
release. Those checks can detect broken transitions, but cannot establish
retail probabilities from a small sample.
