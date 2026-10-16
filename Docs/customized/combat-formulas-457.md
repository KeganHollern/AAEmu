# Combat formulas for r208022

This change resolves [cluster issue #457](https://github.com/KeganHollern/aaemu-cluster/issues/457).
[Research issue #714](https://github.com/KeganHollern/aaemu-cluster/issues/714) tracks the other retail questions.

## Evidence

| Input | SHA-256 |
| --- | --- |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Native `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

The native image uses base address `0x38ff0000`.
Function `0x39571700` maps formula IDs to names.
Function `0x398ac290` evaluates the same equations as both compact databases.
The code reads those equations through `FormulaManager`.
Damage diagnostics at `0x39898330` apply level, height, and range multipliers before armor.
They do not establish every caller or rating conversion.

## Confirmed perception and stealth rules

Formula 5 reduces an NPC's perception range when the player has a higher level.
Its multiplier stays between `0.5` and `1`.
The current NPC range settings and non-player target rules remain in use.

Native functions `0x390b7390` and `0x3935a2b0` select formula 4 in front and formula 10 behind the observer.
The observer's full rotation selects the direction.
Formula 10 returns an absolute range of `0.5` metres.
Observer attribute 94 changes either range by `(100 + value) / 100`.
The full 3D distance must be strictly less than the resulting range.

Functions `0x3989f230` and `0x3989f7b0` rebuild stealth state from active buffs.
The highest `AbLevel` among buffs with `Stealth` supplies the formula's `stealth_level`.
Function `0x3989e080` confirms the runtime ability-level field.
An active `AntiStealth` buff on the target bypasses the stealth range check.
Explicit server invisibility still prevents visibility.

`DetectStealth` is a separate flag.
The reviewed client code does not use that flag to bypass these visibility checks.
Buff tag 100 and the `Stealth` property contain different sets of buffs.
The implementation uses the property, as the native client does.
Character streaming remains separate from these server visibility checks.
Plot condition 16 also remains separate because its native dispatcher was not confirmed.

## Approved server damage rules

Kegan approved these rules for #457. The client proves the equations, but not all server bindings or units.

- Formula 11 uses source height minus target height when `AdjustDamageByHeight` is set.
- Formula 12 applies to ranged damage and uses full 3D distance. Hits below 8 metres receive a `0.8` multiplier.
- Formula 20 applies to player-to-NPC damage. Formula 21 applies to NPC-to-player damage and now returns `1`.
- Formula 24 reduces avoidance by `formula / owner.Facets * 100` percentage points.
- Formula 25 reduces critical chance by the same conversion for the target's Resilience.
- Formula 26 reduces critical damage bonus by `formula / 10` percentage points.

At level 50, 1,000 Focus reduces avoidance by about 3.19 percentage points.
1,000 Resilience reduces critical chance by about 2.74 points and critical damage bonus by 10 points.
Nonpositive ratings or Facets produce no rating reduction.
Probability reductions stay within 0–100 points, and a critical hit cannot deal less than a normal hit because of Resilience.

Damage multipliers apply before armor and final integer conversion.
Damage packets, life steal, threat, and events use that same result.
Fall damage, drowning, and other direct health changes do not use these skill formulas.
The armor constant remains unchanged because the reviewed formula data provides no replacement.

The focused tests cover loaded equations, partial ratings, range boundaries, actor types, buff changes, and NPC acquisition.
The issue records release checks. HUMAN VALIDATION #573 records client checks after deployment.
