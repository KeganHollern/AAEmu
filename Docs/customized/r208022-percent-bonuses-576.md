# Combined Percent bonuses, issue #576

The server now adds simultaneous Percent contributions, then multiplies the base plus flat contributions once. A base of 100 with 2 bonuses of +50% gives 200, not 225. Limits still apply after that calculation.

The shared calculator keeps static flat contributions before dynamic flat contributions. It evaluates each dynamic modifier at read time through the current evaluator. It then adds static and dynamic Percent values to a 100% baseline. The change does not alter `LinearFunc` interpolation, buff duration, unsupported `ManualFunc` behavior, or the movement baseline conversion.

All 81 manual loops in NPC, mate, slave, transfer, and shipyard getters now call that calculator. Their formulas, equipment contributions, initial formula truncation, and final display units stay the same. These getters also receive dynamic values through the shared path. Integer getters truncate the combined result, rather than each Percent term. This follows the native final calculation and avoids repeated rounding between contributions.

## Exact-client evidence

The input is client r208022. The original DLL SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`. The unpacked DLL SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`. Both retain PE timestamp `0x543cb835`, image base `0x38ff0000`, entry RVA `0x008c5d6d`, and image size `0x01cb0a00`. The earlier dump method is not recorded. This work reused that same dump and checked its hash again.

| Native address | Confirmed calculation |
| --- | --- |
| `0x3989fde0` | Adds static Value and Percent contributions to separate accumulators. |
| `0x398a0960` → `0x3989fee0` | Adds evaluated dynamic buff contributions to the same accumulators. |
| `0x398a0a50` → `0x398a0bc0` | Combines equipment contribution groups with the other flat and Percent values. |
| `0x398a0bc0` | Calculates `(base + flat) * combinedPercent / 100`, converts to the native integer value, then applies the limit. |
| `0x395729f0` | Applies the authored inclusive raw bounds. |

The server keeps its existing double precision until each getter's final conversion. This change corrects accumulation, not the separate native fixed-width arithmetic or dynamic interpolation model.

Local evidence is under `.tools-re/combat-movement-20260926/stats-speed` and `.tools-re/combat-rules-20260926/stats` in the cluster workspace. The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

Buffs 926 and 1384 have authored health modifiers of +10% and +40%. Both use Refresh with no shared group. The exact-compact test combines those distinct rows to produce 1500 health from a 1000 base. This is a calculator check, not a claimed client gameplay result.

## Automated and human checks

Focused tests cover positive and negative sums, insertion order, flat values, dynamic lifetime evaluation, unsupported functions, removal, movement slows, raw limits, health, armor, damage, and final integer conversion. Actual getter checks include NPCs, mates, slaves, transfers, and shipyards. The prior 101-health rounding case now stays at 101 when +50% and -50% cancel.

The Release build passed. All 73 focused checks passed with 0 skips: 7 Percent tests, 32 limit tests, 12 XP tests, and 22 cooldown tests. The exact compact checks were enabled. No packet, SQL, compact, or client-content change is needed.

Add these pending checks to HUMAN VALIDATION #573 before closure:

- [ ] Apply 2 compatible Percent health or armor buffs. Record the base and both modifiers. Check the combined value, then remove each buff.
- [ ] Apply 2 compatible Percent movement slows. Check their combined effect and the value after each slow ends.
- [ ] Use a supported time-dependent modifier with a static Percent modifier. Check the value at the start, later in the duration, and after removal.

The checks need named buffs with known modifiers and compatible rules. Use a controlled GM setup when normal gameplay cannot provide the pair. No human gameplay pass is claimed here.
