# Weapon and animation timing, issue #577

The server now uses `MeleeSpeedMul` (54), `RangedSpeedMul` (55), and `AttackAnimSpeedMul` (119) in their separate timing paths. GCD no longer substitutes for these values. The change does not alter cast-time multipliers, cooldowns, projectile travel, or fixed effect delays.

## Exact-client evidence

The input is client r208022. The original DLL SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`. The unpacked DLL SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`. Both retain PE timestamp `0x543cb835`, image base `0x38ff0000`, entry RVA `0x008c5d6d`, and image size `0x01cb0a00`. This work reused the earlier dump and checked its hash. The original dump method is not recorded.

The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`. Local research logs are under `.tools-re/combat-movement-20260926/stats-speed` in the cluster workspace. `timing-getters.log`, `animation-getters.log`, `animation-consumers.log`, `sync-time.log`, `combat-sync-source.log`, and `skill-loader.log` contain the address chains below. Independent disassembly is in `weapon-interval.asm` and `animation-rate.asm`.

| Native address | Confirmed behavior |
| --- | --- |
| `0x398b49e0` | Gets character equipment speed from holdable offset `0x7c`. Slots 15 and 16 use raw attribute 54. Slot 17 uses raw attribute 55. |
| `0x398a0bc0` → `0x395729f0` | Calculates the raw attribute, converts to an integer, and applies its authored bounds. |
| `0x398b49e0` | Adds attribute 75 when holdable offset `0x74` equals slot type 16, which is TwoHanded. It floors the combined bonus at -999. |
| `0x398b49e0` | Returns integer milliseconds as `baseMilliseconds * 1000 / (1000 + rawBonus)`. |
| `0x398b4e80` → `0x398b49e0` | Exposes mainhand, offhand, and ranged intervals as attributes 36, 41, and 46. |
| `0x398aa290` | Converts those interval attributes from milliseconds to seconds for the client display. |
| `0x398b4330` | Uses attributes 54 and 55 with the same denominator rule for the native NPC interval. |
| `0x39355a80` → `0x390b3a80` → `0x398a0bc0` | Gets bounded attribute 119 and returns `max(0.1, 1 + max(-999, rawBonus) / 1000)`. |
| `0x39356440` → `0x39354670` → `0x390bb750` → `0x39156d30` | Gets the animation combat-sync time, divides by the animation rate, and converts to integer milliseconds. |
| `0x39156e50` | Reads the `combat_sync` animation event and stores its time. The resulting descriptor field is the one read by `0x39156d30`. |
| `0x3939b010` | Uses the scaled animation time when the skill descriptor flag at offset `0x1d1` is set. |
| `0x39731870` → `0x39719df0` | Loads SQL column 141, `use_anim_time`, into that flag. The local row begins at `local_2cc`, and `local_fb` is offset `0x1d1`. |
| `0x3938e0b0` → `0x39356440` | Uses the same animation conversion for a plot event's added combat-sync delay. |
| `0x39095440`, `0x39157680`, `0x39157130` | Apply the animation rate to playback or divide animation duration and combat-sync time by that rate. |

The native constants are 1000, 1, 0.001, and 0.1 at `0x3999d1b0`, `0x3999c488`, `0x3999cf30`, and `0x3999ccf0`.

## Authored content and server consumers

All 3 raw attributes have inclusive limits of -800 to 4000. A raw bonus of +500 gives a 1.5 rate. A raw bonus of -600 gives a 0.4 rate. For a 1500 ms weapon, those rates produce intervals of 1000 ms and 3750 ms.

| Attribute | Static rows | Dynamic rows | Examples |
| --- | ---: | ---: | --- |
| MeleeSpeedMul, 54 | 210 | 26 | Buff 651 has -600 in row 10977. Buff 757 has +500 in row 12099. |
| RangedSpeedMul, 55 | 181 | 22 | Buff 651 has -600 in row 10978. Buff 757 has +500 in row 12101. |
| AttackAnimSpeedMul, 119 | 189 | 12 | Buff 4313 has +700 in row 24601. Buff 4343 has -700 in row 24622. |
| TwohandSpeedMul, 75 | 1 | 0 | Buff 4509 has +111 in row 25189. This attribute has no authored limit row. |

`SkillManager.GetAttackDelay` now uses the melee or ranged raw conversion for character auto-attacks. Equipped weapon speed and the current 1500 ms melee and 1800 ms ranged fallbacks stay in use. Two-handed equipment adds attribute 75 after the melee raw limit. The old 400-5000 ms interval clamp is removed. The authored raw limits permit valid intervals outside that range. A 1 ms server floor keeps recurring tasks positive when truncation reaches zero. This floor is a scheduler constraint, not a claimed native interval limit.

`Skill.ScheduleEffects` scales only `FireAnim.CombatSyncTime` when `UseAnimTime` is true. `PlotNextEvent.GetDelay` scales the animation part when `AddAnimCsTime` is true. Both use attribute 119. They keep the separate delay, projectile, controller, and cast-time calculations.

Authored auto-attacks are skill 2 for melee, skill 3 for offhand, and skill 4 for ranged. Their `use_anim_time` flags are true. Skill 2 has no fire animation in its template. Skills 3 and 4 have fire animations 4 and 9. The existing weapon animation override stays in use. This work does not substitute that override's combat-sync time for the template time.

Each timer read uses the shared static and dynamic calculator. Raw attribute results truncate before conversion, as in the native getter. The server still uses its existing double precision and dynamic interpolation. This change does not claim identical native floating-point arithmetic at every intermediate step.

## Recurring task correction

`TaskManager.Tick` chooses the next trigger before the asynchronous callback runs. The old auto-attack callback changed `RepeatInterval` but left that pending trigger unchanged. A speed change therefore kept one obsolete interval.

The new update holds the task manager's execution lock. It changes the pending trigger by the interval difference and keeps the start of that cycle. It only accepts the same queued task object with a positive recurring interval. Paused auto-attacks refresh before their cast, GCD, relation, and range checks. A hit refreshes the interval again because it can change a buff. An unchanged interval keeps the same trigger.

An old auto-attack callback cannot clear a replacement task. Cancellation also checks the queued object, so a reused task ID cannot cancel another task. These changes retain the recurring scheduler. They do not add independent mainhand and offhand timers. Buff changes take effect at the next auto-attack task callback, not through a new immediate buff notification.

Each auto-attack shot now gets a fresh `Skill` object. Delayed impacts keep separate targets, hit state, cancellation state, and timeline IDs. The recurring task keeps the attack options and animation counters. This preserves template, level, cast-time options, callback, weapon selection, and offhand eligibility.

The old task reused one mutable skill for every shot. A later shot could replace the first shot's timeline before its impact. The regression test uses the real recurring task with ranged skill ID 4, projectile speed 40, and targets at 20 m and 28 m. At a 360 ms attack interval, the 500 ms and 700 ms impacts overlap. Each impact retains its target and timeline, ends independently, and keeps the recurring task active.

## Scope limits and separate follow-up

The native offhand interval doubles the base weapon speed before the raw conversion. The current server fires offhand skill 3 from the mainhand callback on every eligible melee tick. A correct independent offhand cadence needs its own scheduling work. This release preserves that behavior instead of adding an unused doubled interval to the current shared cadence. Test different mainhand and offhand weapon speeds in the separate follow-up.

Native NPC interval evidence does not establish how the current server AI must schedule its commands. Its generic skill-delay policy stays unchanged. This release connects the current character weapon-interval path and the shared animation paths. It does not claim a complete replacement for NPC attack scheduling.

`UseWeaponCooldownTime` is separate from `UseAnimTime`. Its loader field is offset `0x1d3`. The disassembly search found its row copy, but no direct timing consumer. The authored auto-attacks set this flag to false. No guessed consumer is added.

The offhand and NPC cadence follow-up is [cluster issue 580](https://github.com/KeganHollern/aaemu-cluster/issues/580).

## Validation

The Release unit project build passed. All 64 focused checks passed with 0 skips: 24 attack timing tests, 3 interval tests, 30 TaskManager tests, and 7 shutdown tests. The exact compact check was enabled. Focused tests cover raw signs and bounds, weapon slots, two-handed contribution, integer conversion, the positive interval floor, static and dynamic modifiers, and removal. The actual skill effect scheduler and plot delay path have tests for animation scaling. Scheduler tests cover a running callback, pending trigger changes, paused attacks, stale task identity, cancellation, and shutdown. Exact-compact tests use the authored +500 and +700/-700 rows.

No SQL, compact, packet layout, or client payload changes are needed. Client display and observed gameplay timing still need human checks. Add these pending checks to HUMAN VALIDATION #573:

- [ ] With a one-handed weapon, record the displayed speed and attack intervals without a speed buff, with a known buff, and after removal.
- [ ] Repeat with a ranged weapon. Check that the ranged modifier changes its interval independently of melee and GCD modifiers.
- [ ] Use a two-handed weapon with buff 4509 or its normal skill source. Compare the displayed interval and the server's attack interval.
- [ ] In a controlled setup, apply animation buffs 4313 and 4343 separately. Check visible animation and damage timing for a normal skill and a plot skill with a combat-sync event.
- [ ] Use a ranged speed buff and switch between 2 distant targets before the first projectile hits. Check that each projectile hits its original target once.
- [ ] Change or remove a speed buff during an auto-attack pause. Resume the attack, switch weapons, stop, and restart. Check that one attack task continues with the new interval.

Use server timestamps when visual timing is not precise enough. The task manager uses a 50 ms tick, which limits observed interval precision. No human timing pass is claimed here.
