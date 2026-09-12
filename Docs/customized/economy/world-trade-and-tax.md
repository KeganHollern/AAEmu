# World trade and house tax

This change addresses issues 487, 488, 498, and the house placement caller in 301.
The client revision is r208022. Human gameplay checks are still needed.

## Trade behavior

Each demand record belongs to one pack template and one destination zone group.
The server saves its ratio, pending sales, and both timer deadlines in `specialty_demand`.
Startup applies elapsed consumption and regeneration ticks. It keeps partial timer periods.
An unsuccessful sale does not change demand.

A sale checks the trader in the player's world, its specialty flag, and the 2.5 meter interaction distance.
It uses the trader's zone group for payment and demand.
The ratio query contains the equipped pack template ID. It has no trader ID.
The server checks that ID against the equipped pack and uses the current reachable specialty trader's zone.
Without a valid current trader, the query uses the player's zone.

An authored NPC bundle takes precedence over the route matrix.
A mapped bundle must contain the pack. An unmapped trader accepts only normal specialty packs with a matrix route.
The matrix fallback rejects a sale in the pack's origin zone. An explicit bundle keeps its authored rule.
The loader reads `vendor_exist` from its own column.

The server rounds the grade refund first, then the route price.
It rounds each positive result with `floor(value + 0.5)`.
The existing 5 percent interest and 80/20 maker share remain in use.
The maker share uses the active feature flag.
Commerce applies its current labor multiplier to the 60 labor base cost.

One SQL transaction saves the consumed pack, payout mail, account labor, and demand.
A known failure restores the prepared inventory, mail, and labor state.
An unknown commit result keeps the prepared state and passes the error to the central consistency stop.
Only a committed sale sends asset notifications and grants Commerce experience.

## House behavior

Placement pays one period and sets a second period as the grace window.
With the default settings, the first due date is placement plus 7 days.
The protection end date is placement plus 14 days.
This change does not alter old house dates in bulk.

An owner cannot demolish a house on or after its tax due date.
The automatic expiry path still removes an unpaid house after the grace window.
The tax status packet now sends true when the current period is paid.

House placement consumes its design and full tax payment in one inventory mutation.
One SQL transaction saves the house and the player's changed assets.
An insufficient or reserved payment cannot create a free house.

Tax mail payment saves the asset debit, protection extension, bill removal, and receipt in one SQL transaction.
The receipt keeps the bill ID, house, payer, amount, currency mode, fee setting, dates, and payment time.
The receipt key rejects a repeated bill. The mail allocator keeps receipt IDs reserved after restart.
A stale amount creates a new quote and consumes no payment.

`World.HouseLateFeePercent` controls the approved custom fee. Its default is 10.
The accepted range is 0 through 100 percent.
A nonzero value adds `ceil(weekly_tax × percent / 100)` once during the grace window.
It does not compound. The mail quote, tax status, payment check, and receipt use the same setting.
The user approved 10 percent for this server. It is not a confirmed retail value.

## Exact client evidence

Research used `client/bin32/x2game.dll` with SHA-256
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The analyzed memory-layout dump has SHA-256
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Both PE timestamps are `543cb835`. The dump creation procedure still needs a provenance check.
Native addresses below use image base `0x38ff0000`.

| Function | Confirmed behavior |
| --- | --- |
| `FUN_395ac920` | Bundle lookup takes precedence. Only unmapped normal packs use the matrix. |
| `FUN_398a9bf0` | The bundle formula uses profit, ratio, and grade refund. |
| `FUN_398a9b00` | The matrix formula rejects the origin zone. |
| `FUN_398a9a80` | The matrix lookup uses row origin and column destination. |
| `FUN_397cd2c0` | The client rounds the grade refund before the route calculation. |
| `FUN_393c05f0` | C2G `0x043` sends the equipped pack template ID. |
| `FUN_393c14c0`, table `0x39ac7c70` | The `specialty_trader` interaction owns that query. The global route UI uses `GetSpecialtyRatioBetween`. |
| `FUN_394fb8a0` | C2G `0x042` sends the selected trader object ID. |
| `FUN_391d7c20` | The house tax handler forwards the paid flag to the UI. |
| `maintain_window.alb`, function `UpdateMyHouseTaxInfo` | A true paid flag displays the paid state. |
| `read_mail.alb`, house tax body | The client displays the supplied total. It does not calculate the penalty. |

Client text confirms negotiation and a late-payment penalty.
It does not state the negotiation chance, multiplier, or late-fee percentage.
The compact tables and retained upstream history did not resolve those values.
The user selected custom rules for this server.
The late fee is 10 percent of the weekly tax, rounded up once after the due date.
Commerce rank sets a negotiation chance of 1 percent per rank, capped at 7 percent.
Each sale gets 1 roll after its quote and labor checks.
A successful roll adds 5 percent of the demand-adjusted price before interest and maker sharing.
The bonus rounds down to whole copper or whole reward items.
The payout mail records the bonus amount and final payout in the same transaction as the pack and labor debit.
A repeated request after a committed sale finds no pack and cannot make another roll.
A failed checkpoint restores the pack and removes the prepared payout.
These are custom server rules, not claims about retail rates.

The exact client `locale_helper.alb` maps the payout mail through `body()` at source lines 935 through 967.
It subtracts argument 5 from argument 3 before it sets `applyPriceMoney`.
Argument 3 must therefore contain the price with the bonus included.
For a demand price of 1430 copper, the server sends price 1501, bonus 71, and total 1576.
The client displays base 1430, bonus 71, and total 1576.
The script SHA-256 is `944dc8076da065746f4a65f767bd9870c1bb5138b641004edea2fc89e49321b9`.

## SQL review

Two additive updates create `specialty_demand` and `house_tax_receipts`.
The base schema includes both tables. No update changes current character, item, mail, or housing rows.
The first successful sale creates a demand row. The first successful tax payment creates a receipt.
The startup updater uses the current `AutoApplyUpdates=true` policy.
No live SQL was applied during development.

## Checks

Focused unit tests cover demand catch-up, rounding, failed settlements, duplicate sale rejection, trader-zone demand, and payment reservations.
Mail tests cover failed tax checkpoints, duplicate bills, stale quotes, owner checks, and one-time payment.
MySQL tests check joint rollback and commit for demand, labor, items, mail, house dates, and receipts.
They also check demand reload, receipt replay rejection, and repeated additive updates.

1. Sell a normal pack at an unmapped trader and check its authored matrix route.
2. Sell a bundle pack at its mapped trader and compare the mail amount with the client quote.
3. Sell beside a zone border and check the trader's destination demand.
4. Restart the Game server and check that demand does not reset to 130 percent.
5. Place a house and check that the first due date is 7 days later.
6. Pay one tax bill, repeat the same request, and check one debit and one date extension.
7. Try owner demolition after the due date and check error 567.
8. Pay an overdue bill. Compare the quote, debit, and receipt with the weekly tax plus its rounded-up 10 percent fee.

## Doodad purchase evidence for issue 311

The exact r208022 native path confirms the gold fallback.
`FUN_393b80d0` reads the purchase descriptor in this order:
`coin_count`, `coin_item_id`, `count`, `currency_id`, and `item_id`.
It selects the gold dialog when `coin_item_id == 0 OR coin_count < 1`.
It selects the item-coin dialog only when both the coin item and coin count are positive.
The static coin sentinel at `0x3a17e6a8` equals 0.

`FUN_393b9080` writes `moneyString` from the output item's price at descriptor offset `0x84`, multiplied by the output count.
`FUN_393bc4e0` uses the same multiplication for its affordability check before the request.
Both functions use the count copied from the purchase descriptor.
The currency ID passes through the gold dialog unchanged.
`FUN_393b92a0` displays the coin item name and exact coin count for the item-coin branch.
The loader `FUN_3966f260` confirms the descriptor field order against the compact table query.

## Dormant specialty goods and records in issue 488

The active trader quote and the global route window use separate requests.
`FUN_393c14c0` registers `FUN_393c05f0` for the `specialty_trader` interaction, skill 16376.
That handler sends C2G `0x043` with the equipped pack template ID.
The route window calls `GetSpecialtyRatioBetween` with its selected origin and destination.
A valid current trader can supply the quote destination without a change to the route window.

The same native registry keeps an old `specialty_store` interaction for skill 17135.
Its handler, `FUN_393bf840`, sends C2G `0x044` with a 3-byte object ID.
No authored `npc_interactions` row uses skill 17135.
The only compact skill references are `doodad_funcs` rows 9975 and 9976.
Both use `DoodadFuncPurchase`, which has the separate purchase dispatch described above.
The current NPC script exposes the specialty sale control. It has no old specialty-store control.

The exact client keeps these incomplete response paths:

| Response | Native behavior |
| --- | --- |
| G2C `0x09a` | `FUN_39185ba0` passes at most 20 goods to `FUN_394fcbc0`. The function replaces a private cache and resolves item templates. |
| G2C `0x09b` | `FUN_39181ff0` calls `FUN_394fb300`, which immediately returns. |

The bounded native scan found no goods-cache reader beyond its destructor.
The complete script scan found no specialty purchase or record API caller.
The script scan scope and manifest hash appear in `dominion-and-dormant-packets.md`.
Current sale scripts use `GetSpecialtyRatio`, `GetSpecialtyRatioRefund`, and `SellBackPackGoods`.
The route script uses `GetSpecialtyRatioBetween`.

The server keeps `CSListSpecialtyGoodsPacket` as an unsupported old request.
`CSBuySpecialtyItemPacket` and `CSSpecialtyRecordLoadPacket` have no confirmed r208022 opcode and stay unregistered.
The G2C goods and record constants do not imply a usable client feature.
No invented goods catalog, purchase settlement, or record packet was added.
An active implementation needs a confirmed caller, complete response contract, and authored purchase data.

## Authored craft duration in issue 469

The current client and server compact snapshots agree on all 7005 joined craft and skill rows.
There are 6471 crafts whose `cast_delay` differs from the skill's `casting_time`.
The other 534 rows have equal values. Authored craft delays range from 500 through 15000 milliseconds.
Craft 4107 uses skill 15086. Its craft delay is 15000 milliseconds, while the skill delay is 5000 milliseconds.

The client compact SHA-256 is `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The check used this query against both snapshots:

```sql
SELECT crafts.id, crafts.skill_id, crafts.cast_delay, skills.casting_time
FROM crafts JOIN skills ON skills.id = crafts.skill_id;
```

Craft execution must use `Craft.CastDelay` as its base before the current proficiency multiplier.
Ordinary skill execution keeps `SkillTemplate.CastingTime`.
`CraftDuration.GetBaseMilliseconds` selects the base without a change to the shared skill template.
The call site must use a value on the individual skill instance.
This prevents one recipe from changing another recipe that shares its skill.
No packet contract or compact data change is needed.

The helper tests cover craft 4107, shared skill templates, and ordinary instant skills.
The execution test must also check the final scheduled duration after proficiency.
After release, craft 4107 at novice proficiency and check a 15-second base duration before other active speed bonuses.

## Paid construction progress in issue 463

House construction saves its changed `current_step` and `current_action` with the skill's labor and material payment.
A known checkpoint failure restores the exact step, action counters, model, and dirty flag.
Insufficient labor changes no construction state.
Bound doodad work and progress messages follow the commit.
The current startup reconciliation creates missing bound doodads for a completed house.
This covers a process stop after the house commit and before its door or window creation.

Shipyard progress joins the same labor batch and restores its exact state on a known failure.
Shipyards have no current SQL progress store. This change does not add one.
The launch action stages its output item before payment commits.
Only a successful commit starts the ceremony task and sends its progress message.
A wrong construction skill changes no progress.
A foreign owner, full bag, or failed checkpoint cannot spend labor for the launch.

The construction tests cover intermediate progress, completion, insufficient labor, failed checkpoints, and ship launch payment.
The MySQL cases check house progress, labor, and material in one transaction.
No schema or compact change is needed for construction progress.

### Temporary doodad creation in issue 463

Paid `SummonDoodad` interactions and `SpecialType.SpawnDoodad` effects keep their temporary lifetime.
Their initial phases and world spawn start after the labor and material transaction commits.
A known failed commit releases the unused object ID.
A missing doodad template or invalid placement cancels the paid batch.
These changes add no persistent doodad rows, schema changes, or compact changes.

The MySQL tests check successful creation, insufficient labor, and a failed checkpoint.
They check account labor, material rows, initial phase timing, world publication, and object ID reuse.

### Direct player doodad placement in issue 463

`CSCreateDoodadPacket` passes its validated labor cost to the placement transaction.
Public farms and permitted house land keep their current zero-cost rules.
The transaction checks the exact bag item again before it allocates a doodad ID.
Reserved quantities and items in a bank cannot pay for placement.
The transaction saves labor, the source item, the new doodad, and any empty coffer container together.
A known failure restores the source item and labor, removes the new container, and releases the new doodad IDs.

Stackable sources use 1 unit from the requested item.
Non-stackable sources move to `SystemContainer`, which preserves their identity, UCC, and other details for `DoodadFuncRecoverItem`.
The manager sends 1 item-use event after the commit.
It does not send another event for each alternative source template in `item_spawn_doodads`.
System-created doodads with no source item do not consume player inventory.
The current compact contains 10 doodads with multiple source templates.
Doodad 272 maps to item templates 501, 16158, 16237, and 1449.

The saved new doodad includes its initial phase and growth times.
Phase functions and world publication start after the commit.
The current startup loader calls `InitDoodad` for saved player doodads, so it can resume a committed placement after a process stop.
The change uses the current `doodads`, `items`, `item_containers`, and account labor columns.
It adds no schema or compact change.

The focused checks cover failed SQL after doodad writes, insufficient labor, reserved sources, bank sources, coffer containers, and exact item details after reload.
After release, place and recover a non-stackable item with UCC, then reconnect and repeat the recovery check.
