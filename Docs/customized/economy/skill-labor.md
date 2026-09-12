# Paid skill settlement

Issue 463 concerns products granted before the labor check at skill completion.
The old completion path skipped the debit when the account no longer had enough labor.
The player could then keep the products without the labor cost.

`Skill.Use` now rejects a paid skill when the account lacks its proficiency-adjusted labor cost.
The completion path checks labor again under the persistence and account locks.
The skill stages its labor, inventory changes, experience, vocation points, and persistent harvest state in one SQL transaction.
Each asynchronous plot node opens its own synchronous batch.
The first reward or labor marker pays the skill cost once.
Duplicate normal completion cannot charge or grant again.

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

Issue 469 uses the authored `Craft.CastDelay` on the individual skill instance.
The normal skill template remains unchanged.
The server applies proficiency and skill modifiers once, then uses that duration for the packet and scheduled task.

## Data audit and open support work

The server compact contains 1175 skills with `consume_lp > 0`, including 177 plot-only skills.
Its SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The effect audit joins `skills`, `skill_effects`, and `effects`, plus `plot_events` and `plot_effects` for each assigned plot.
The audit includes all events in each plot. The runtime check follows reachable nodes.

The first batch implementation supports the effect types of 1135 paid skills.
It rejects the other 40 skills before effects because their state does not yet join the transaction.
Those skills include delayed child skills, temporary doodad creation, tempering, and test or incomplete effects.
This count does not prove that every interaction function is safe.
Temporary doodad publication and the separate direct placement packet still need focused follow-up changes before release.
The direct placement path must preserve its current public-farm and house labor waivers.

No schema or compact change is part of this batch implementation.
The existing SQL tables store the staged results.

## Checks

The focused unit group passed all 155 cases.
The MySQL group passed all 104 base cases after test fixture corrections.
The focused tests cover initial zero labor, labor lost before completion, failed grants, failed SQL, and duplicate completion.
The tests also cover asynchronous plot rewards, source-item quest progress, and nested checkpoint rejection.
The MySQL cases compare the account labor, material items, output items, experience, vocation points, and persistent doodad state.
Separate cases cover persistent buff addition, replacement, dispel, construction, and the committed callback failure window.

The duration execution test checks a 15000 ms craft base with a 0.8 proficiency multiplier.
It expects a 12000 ms task and packet duration, including when the shared skill has a zero cast time.

After release, test one gather and one craft at zero labor.
Then start a delayed gather, remove its required labor, and check that completion gives no reward.
Check one harvest after restart, one regrade, one long-duration paid buff, and one house construction step.
Check craft 4107 against its 15-second base before active speed bonuses.
