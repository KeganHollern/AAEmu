# Manual buff modifiers (#641)

The r208022 server now loads `manual_funcs` and evaluates their ordered integer
values through the current dynamic-bonus path. Values are resolved once at
load time. Evaluation does not parse text or query a manager.

## Confirmed client rule

The native loader at `0x3972a610` reads `manual_funcs.value_list`.
`0x3972a800` attaches ManualFunc descriptors to dynamic modifiers.
The evaluator at `0x3989fee0` selects the current tick index, clamps it to the
first or last entry, and adds the selected value to the requested accumulator.
`0x398a0960` calls it only for a positive runtime duration and tick interval.
The ability level and charge count do not select a value.

Runtime initialization at `0x3989e080` derives the index from elapsed time
divided by the tick interval. `ClientBuffMan::Update` at `0x393888b0` advances
that index at each elapsed tick boundary. It catches up one tick per frame.
The server calculates the index directly at each stat read, without reproducing
that temporary frame delay. The native stack count is separate from Charge.
All 3 authored ManualFunc buffs have maximum stack count 1.

| Buff | Function | Tick | Authored values | Result while active |
| --- | --- | --- | --- | --- |
| Leg Injury (930) | 11 | 1,000 ms | -1000; -1000; -700; -350 | 0x speed for 2 seconds, then 0.3x for 1 second, then 0.65x until expiry at 5 seconds. |
| Breeze Wings (1639) | 15 | 25,000 ms | 500; 150 | 1.5x speed for 25 seconds, then 1.15x until expiry at 30 seconds. |
| Running Amok (739) | 22 | 0 | 100 | No dynamic contribution. The native duration/tick gate skips it. |

The speed results assume no other movement modifiers. All rows target
`MoveSpeedMul` (attribute 10), with flat modifier type 0. Both compacts agree.
The original issue assigned the values to the wrong buffs. Those links are corrected.

## Lifecycle correction

The previous buff scheduler waited a full tick interval after the last tick.
Breeze Wings could last about 50 seconds instead of 30 seconds. Leg Injury
could last about 6 seconds instead of 5 seconds. The scheduler now bounds the
next delay by the time left and removes a finite buff at its expiry callback.
The final full tick still runs before removal. Non-expiring ticked buffs keep
their current scheduling behavior.

Refresh keeps the current buff object and resets its clock. Registration first
removes old bonuses for that buff index. Dispel removes the dynamic contribution.
No SQL, compact, packet, or client-content change is needed. LinearFunc
calculation remains unchanged.

## Evidence and validation

This is a downstream r208022 change. Its data and native gates need review before
use with another client version.

- Native image SHA-256: `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
- Native image base: `0x38ff0000`. Addresses above are virtual addresses at that base.
- Client compact SHA-256: `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
- Server compact SHA-256: `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

Tests cover list parsing, boundaries, missing data, all 3 authored buffs,
movement values, refresh, removal, final partial intervals, and expiry.
Real-client checks belong in [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
