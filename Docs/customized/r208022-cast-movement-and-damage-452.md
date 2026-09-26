# Cast movement and damage rules for r208022

This change resolves aaemu-cluster issue #452. It applies to normal cast tasks and authored plot cast waits.

## Exact inputs

- Client revision: `r208022`, from `client/history.txt`.
- Packed `x2game.dll` SHA-256: `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
- Runtime dump SHA-256: `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
- Both binaries have PE timestamp `0x543cb835` and image size `0x1cb0a00`. Addresses below use dump base `0x38ff0000`.
- Server compact SHA-256: `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
- Server base: `ca6d15ad4ead116eecc2c479b7849a8b4f9fddf4`.
- Local native exports and test logs: `.tools-re/combat-movement-20260926/cast/` in the cluster workspace.

## Confirmed evidence

The native actor movement function `0x39404d50` calls cast stop `0x393966d0` and channel stop `0x393967c0` when movement occurs. Rotation uses a separate gate at `0x39395d90`. That gate checks the template flag at `+0x15a`, which the template loader maps to `stop_casting_by_turn`. It also checks effect kind `0x22`. This change uses the authored turn flag. It does not add that separate effect-kind branch.

`source_cannot_use_while_walk` is a separate admission flag. It is false for ordinary cast skills such as `10107`. It cannot identify the casts that must stop after movement starts.

The compact defines these formulas:

```text
CastingTolerance, kind 30, all 7 owner kinds: 100
CastingDelayTime, formula 3:
300 * (100 / casting_tolerance) * (0.42 * floor(damage_percent / 5))
CastingCancelPercent, formula 2:
0 * (100 / casting_tolerance) * (1 + (1 * floor(damage_percent / 10)))
```

The compact has 18 static modifiers for `CastingTolerance` attribute `89`. The change applies these through the normal unit bonus calculation. It does not invent a damage cancellation chance or a large-hit threshold.

Normal skills use their `casting_delayable` flag. Plot waits use their own `casting` and `casting_delayable` flags. Of the current plot edges, 15 have both flags. Ordinary delays and channel waits do not receive damage delay.

## Delay packet

`SCCastingDelayed`, G2C level `1`, opcode `0xa6`, has this body:

| Offset | Wire type | Meaning |
| --- | --- | --- |
| 0 | `u16` | Normal skill timeline ID, or `0`. |
| 2 | `u16` | Plot timeline ID, or `0`. |
| 4 | `u32` | Added delay in milliseconds. |

The body has 8 bytes and no optional fields or trailing data.

Registration `0x391bf1e0` installs the handler. Factory `0x391ab980` allocates a `0x18` byte object with opcode `0xa6`. Parser `0x397ca570` reads `skillId/tl`, `plotId/tl`, then `delay`. Handler `0x391d5d70` passes those fields to normal and plot timeline consumers `0x39395760` and `0x3938c520`. Each consumer adds the delay to the active cast duration. Each also adjusts animation speed from the old and new time left.

The parser uses stream slot `+0x40` for timeline IDs and `+0x3c` for delay. As an independent contract check, native `SCCastingStopped` parser `0x397ca500` uses those same primitives for its known `u16` timeline and `u32` duration body. The server already uses that body. This is a matching-contract check. The raw stream helper was not separately decompiled.

## Server choices and lifecycle

The following choices are server inferences from the authored data and native lifecycle:

- `damage_percent` is health lost after absorption, divided by maximum health, multiplied by `100`.
- Each eligible hit adds the formula result to the current deadline. The integer conversion drops the fractional millisecond.
- At default tolerance, damage below `5%` adds no delay. A `5%` hit adds `126 ms`. A `10%` hit adds `252 ms`.
- Fully absorbed hits, healing, zero damage, lethal hits, expired waits, and completed waits add no delay.
- Every active, eligible branch in one plot receives the same delay. The server sends 1 packet for that plot timeline.
- Accepted actor movement cancels an active cast or channel. Instant skills and ordinary plot or projectile delays remain active.
- Local coordinates distinguish actor movement from vehicle movement. A parent change cancels an active cast, so mount or dismount cannot bypass the rule.
- An active server skill controller owns forced movement. Its movement does not cancel the skill that owns the controller.
- Position and rotation comparisons use the packet representation to avoid cancellation from quantization alone.

A synchronized cast window separates completion, cancellation, and delay. An old queued task cannot fire for a replaced task. If a queued callback reaches an extended deadline too early, a new wakeup waits for the rest of the cast. The scheduler never reuses the ID of the callback in progress.

Successful new-cast admission stops the previous timed cast before the new cast starts. Deferred plots publish a pending state before their worker starts. That state remains pending until the initial phase registers its waits. Replacement admission can cancel pending work, and auto-attacks pause for pending or active cast/channel waits. Movement still needs an authored active wait. A mixed normal/plot skill keeps its shared timeline until both paths end. Each path retains its current callbacks. Failed admission keeps the previous cast. Callbacks run outside the timing locks. Cleanup only clears a task or plot that still belongs to that skill. Channel cancellation ends the skill once.

The plot queue stores a window for each authored cast or channel edge. Cast and channel state come from those windows. A cancelled window keeps its former phase for the stop packet and cooldown decision. Ordinary plot delays no longer appear as channels.

This change does not change compact data, attack speed, cast speed, global cooldown rates, or damage formulas outside cast delay.

## Automated checks

The focused tests cover:

- Movement without a client stop packet, rotation flags, coordinate quantization, and parent changes.
- Damage steps, repeated hits, tolerance modifiers, absorption, lethal damage, and unmarked casts.
- Cast completion against cancellation, delayed wakeups, old callbacks, and replacement identity.
- Plot fan-out, ordinary delays, a live plot queue, cancellation packets, and phase-based cooldown behavior.
- Successful and rejected replacement admission, timeline release, and channel cleanup.
- Both timeline fields, millisecond units, maximum field values, and complete packet consumption.

The Release build passed with 0 errors and 71 warnings. The skill suites passed 390 tests with 0 skips, with the exact compact enabled. All 43 duel tests passed. The TaskManager suites passed 37 tests with 0 skips. `git diff --check` passed. The release record supplies the source commit and the combined release checks.

## Pending human checks for #573

These checks need the published server and the current r208022 client. A GM can grant the listed skill IDs for a test character. The IDs refer to the exact compact above. They avoid dependence on translated skill names.

1. Cast normal skill `10107` at a valid target, then move before its `2000 ms` cast ends. The cast must stop without damage or resource settlement.
2. Start plot skill `10752`, which uses plot `280`. Move during its authored `1000 ms` cast wait. The cast bar must stop, and the waiting attack must not fire.
3. Repeat a stationary cast of `10107` and plot cast `10667` or `10670`. Let an NPC remove at least `5%` of maximum health in one hit. The cast bar and attack must both receive delay. Small hits below `5%` must not move the bar.
4. Rotate during a normal `10107` cast without movement. The cast must continue. Move after a projectile fires. The projectile must still reach its target.
5. Start channel skill `10372`, then move. Its channel effect must end once. Start another skill at once. No old task must stop that new skill.
6. Start a cast while on a moving vehicle without local actor movement. The cast must continue. Mount, dismount, or move locally during a cast. The cast must stop.
7. Enable auto-attacks, then start a plot cast. The auto-attacks must pause through the cast and resume afterward. Use an instant skill during normal movement. Confirm that it still works. Check a server-controlled movement skill separately for an unwanted interruption.

Damage-delay values, forced-movement behavior, channel cleanup, and visual synchronization still need these human checks. No retail runtime capture confirms the inferred server formula inputs or plot fan-out rule.
