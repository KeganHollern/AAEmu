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
The server checks that ID against the equipped pack and uses the player's zone for this query.

An authored NPC bundle takes precedence over the route matrix.
A mapped bundle must contain the pack. An unmapped trader accepts only normal specialty packs with a matrix route.
A sale in the pack's origin zone is invalid.
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

`World.HouseLateFeePercent` controls a custom fee. Its default is 0.
The accepted range is 0 through 100 percent.
A nonzero value adds `ceil(weekly_tax × percent / 100)` once during the grace window.
It does not compound. The mail quote, tax status, payment check, and receipt use the same setting.
A value of 10 is a proposed server rule. It is not a confirmed retail value.

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
| `FUN_394fb8a0` | C2G `0x042` sends the selected trader object ID. |
| `FUN_391d7c20` | The house tax handler forwards the paid flag to the UI. |
| `maintain_window.alb`, function `UpdateMyHouseTaxInfo` | A true paid flag displays the paid state. |
| `read_mail.alb`, house tax body | The client displays the supplied total. It does not calculate the penalty. |

Client text confirms negotiation and a late-payment penalty.
It does not state the negotiation chance, multiplier, or late-fee percentage.
The compact tables and retained upstream history did not resolve those values.
Negotiation remains disabled. The default late-fee percentage remains 0.
These acceptance items remain open until the user selects a custom rule or exact evidence resolves them.

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
8. If a custom late fee is enabled, compare the displayed quote, debit, and receipt.
