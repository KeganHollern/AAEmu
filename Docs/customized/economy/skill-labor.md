# Paid skill settlement

Issue 463 concerns products granted before the labor check at skill completion.
The old completion path skipped the debit when the account no longer had enough labor.
The player could then keep the products without the labor cost.

`Skill.Use` now rejects a paid skill when the account lacks its proficiency-adjusted labor cost.
The completion path checks labor again under the persistence and account locks.
Each synchronous effect batch saves its labor, inventory changes, experience, vocation points, and persistent harvest state in one SQL transaction.
Each asynchronous plot node opens its own synchronous batch.
The first reward or labor marker pays the skill cost once.
Duplicate normal completion cannot charge or grant again.
The server does not hold a database transaction open across a plot delay.

Known failures restore the staged state and suppress success callbacks.
An unconfirmed SQL commit stops later persistence.
A callback failure after a known commit also stops persistence, so a later save cannot overwrite the committed result.
The server must restart from durable state after either consistency stop.

Item grants, consumption, money changes, regrade, and item conversion use the active inventory mutation.
Harvest phase changes and associated item deletion join the same transaction for persistent doodads.
Phase timers, replacement spawns, quest progress, and success packets wait for the commit.
Buff and dispel effects prepare a detached buff list and save it before live callbacks.
Construction stages house progress, shipyard progress, and the ship launch item.
Shipyards retain their current temporary lifetime.
Persistent doodad placement saves the source item, labor, placement record, and any house attachment together.
Public-farm placement and permitted house placement keep their current labor waivers.
Temporary doodads and replacement spawns become visible after commit.

## Automatic labor rewards

`CharacterLaborMutation` prepares automatic rewards before the character and account records enter the transaction.
These rewards include character experience, level, ability experience, proficiency points, and the consumed-labor counter.
The common path covers skill effects, trade settlement, and direct doodad placement that use this mutation.
Known failure restores these values with the labor debit.
Packets, achievement notifications, and level-change callbacks run only after a known commit.
They do not apply the experience or proficiency increase a second time.

Labor experience uses the authored formula, proficiency multiplier, world experience rate, `ExpMul`, and `ExpByLaborPowerMul`.
The server uses the authored `ExpMul` limit and adds no guessed limit for `ExpByLaborPowerMul`.

## Child skills and paid actions

Supported immediate child skills join the parent transaction instead of starting a separate paid cast.
Each paid child stages its own labor and automatic rewards in that transaction.
Supported child buffs and loot use the same prepared state as the parent.
Delayed presentation-only child skills wait until the parent commit.
An unsupported child rejects the parent before effects.

Compose saves the source item, sheet music, labor, and rewards together.
Bot reports save distinct reporter pairs, counters, suspect buffs, labor, and rewards together.
The startup loader restores the distinct pairs, so restart cannot permit a duplicate paid report.
Paid and free appeals use the same transaction to remove the report pairs and suspect buffs.

## Craft duration

Issue 469 uses the authored `Craft.CastDelay` on the individual skill instance.
The normal skill template remains unchanged.
The server applies proficiency and skill modifiers once, then uses that duration for the packet and scheduled task.

## Data audit and explicit exclusions

The server compact contains 1175 skills with `consume_lp > 0`, including 177 plot-only skills.
Its SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The effect audit joins `skills`, `skill_effects`, and `effects`, plus `plot_events` and `plot_effects` for each assigned plot.
The audit includes all events in each plot. The runtime check follows reachable nodes.

The final effect audit accepts the effect types of 1160 paid templates.
The server rejects the other 15 templates before effects because their state does not join the transaction.
This count does not prove that every interaction function is safe or that all authored content works.

| Skill IDs | Excluded content | Source and compact evidence |
| --- | --- | --- |
| 10980, 16891, 16892, 16893, 16894, 19316, 19317, 19318, 19319, 19334 | Labor test skills | All 10 names identify test content. Their `AddLaborPower` effects grant 500 labor. |
| 13202 | Pickpocket | Item 15717 invokes this hidden skill. The checked compact item and grant references contain no acquisition route for that item. |
| 13661 | Dominion claim | The server lacks the authored ownership lifecycle. This batch removes the old sample ownership and pack consumption. |
| 15446 | Combine Magic Equipment | `RenewEquipment` remains a preexisting log-only stub. Its old effect made no equipment change. |
| 20322 | Hit Mandragora | Hammer items 26390 and 26587 use proc 70. The old proc cooldown and target code already prevent normal use. |
| 23417 | Crime-point test | The compact identifies test content. `GiveCrimePoint` remains a preexisting log-only stub. |

Pickpocket has `plot_only=t` and a null `plot_id`.
The old `Skill.Use` checks `PlotOnly` only when a plot exists, so its normal child effects can run.
Direct requests and manual item grants can reach this old content.
The batch now rejects those requests. The absent plot does not prove that the old code was unavailable.

Mandragora has authored hammer acquisition routes, but every new proc starts with `LastProc=DateTime.MinValue`.
The old `UnitProcs.RollProcsForKind` cooldown condition skips such a proc.
The old `ItemProc.Apply` also selects its owner as the hostile skill target.
A future proc correction must add paid damage settlement before this skill can work through its normal route.

The review found no ordinary gameplay route that loses a working effect solely because of these 15 exclusions.
The exclusions still change direct requests to prototype, test, or defective content.

The batch adds `SQL/updates/2026-09-12_aaemu_game_bot_reports.sql` and the matching base-schema definition.
This table stores distinct reporter pairs and has no historical backfill.
It has no foreign keys because ordinary character saves use `REPLACE`.
The rest of the staged state uses current SQL tables. This change does not modify either compact database.

## Checks

The focused tests cover initial zero labor, labor lost before completion, failed grants, failed SQL, and duplicate completion.
The tests also cover asynchronous plot rewards, source-item quest progress, and nested checkpoint rejection.
The MySQL cases compare account labor, material items, output items, experience, proficiency, vocation points, and persistent doodad state.
Separate cases cover persistent buff addition, replacement, dispel, construction, and the committed callback failure window.

The automatic reward group passed 10 MySQL cases.
The related focused unit groups passed 41 labor cases and 12 experience cases.
The music, report, and related paid buff group passed 24 MySQL cases.
Direct doodad placement passed 10 MySQL cases and a related 23-case unit group.
These focused results do not replace the final combined build and test run recorded with the release.

The duration execution test checks a 15000 ms craft base with a 0.8 proficiency multiplier.
It expects a 12000 ms task and packet duration, including when the shared skill has a zero cast time.

After release, test one gather and one craft at zero labor.
Then start a delayed gather, remove its required labor, and check that completion gives no reward.
Check one harvest after restart, one regrade, one long-duration paid buff, and one house construction step.
Check craft 4107 against its 15-second base before active speed bonuses.
Check that experience and proficiency remain after restart following a paid gather or trade sale.
Check one sheet music save, one bot report, and one appeal after restart.
Check paid placement outside protected land and free placement in a permitted house or public farm.

## Tempering

The 2 paid tempering skills now stage both equipment scale fields with labor and the source item.
The server marks the equipment as dirty before the SQL checkpoint.
Known failure restores both fields and suppresses the success notice.
The 2 MySQL cases check success and failure through a fresh item load.

The compact rows use skill 25871 with scale bounds 101 and 110.
Skill 26943 uses bounds 105 and 115.
The change keeps the current random range rule.
