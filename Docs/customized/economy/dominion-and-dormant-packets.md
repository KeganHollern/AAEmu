# Dominion state and dormant economy requests

This change covers the minimum state in issue 143 and the explicit unsupported paths in issue 139.
It does not add a claim, siege transfer, tax vote, or craft payment service.

## Supported state

The server reads all authored `siege_zones` from the r208022 compact.
The 6 zone groups are 33, 34, 43, 44, 54, and 56.
Their siege zone IDs are 1, 2, 3, 4, 6, and 7.
The new `dominion_states` table saves each territory with owner 0, tax rate 0, and tax balance 0.
The startup loader adds only missing rows. It does not replace saved balances.

Login sends a full `SCDominionDataPacket` before each `SCDominionTaxRatePacket`.
The full record contains absent ownership, house, monument, coordinates, and siege state.
Its nested participant arrays are empty. Both announcement flags are false.
The record gives the client a valid target for the current zero tax rate.
The server does not send a national-rate event for an unclaimed territory.

The prototype `DeclareDominion` action no longer broadcasts sample ownership or consumes an equipped pack.
Unsupported requests send a clear system message.
The new loader rejects nonzero ownership or a nonzero tax rate.
Those values need a confirmed claim target and an ownership lifecycle before the server can use them.

The persisted balance represents supported house-tax money. No unclaimed territory receives revenue.
The current house-tax transaction records its debit and paid period in `house_tax_receipts`.
A future territory revenue rule must write its balance and receipt in the same transaction.
It must not allocate an unproved share of current house tax.

## Exact r208022 contracts

The binary identities match `world-trade-and-tax.md`.
The following functions come from that exact memory-layout client dump.

| Native function | Contract |
| --- | --- |
| `FUN_397afd00` | G2C `0x1d` writes the dominion record, then two Boolean flags. |
| `FUN_3983a470` | The complete dominion record contains 258 bytes when both participant arrays are empty. |
| `FUN_3961e270` | Each position uses two Int64 coordinates and one float height. |
| `FUN_3962a5b0` | The territory section uses two UInt32 IDs, two 1-byte limits, and four 2-byte radii. |
| `FUN_39839740` | The timer section uses five Int32 durations, two 8-byte times, and one Int32 field. |
| `FUN_39839f40` | A 1-byte period precedes two participant records. |
| `FUN_39839c80` | A participant has an ID, 3-byte object ID, position, two 1-byte fields, and a bounded list. |
| `FUN_39839c10` | The list has a 1-byte limit, 1-byte count, and UInt32 IDs. |
| `FUN_391dfb30`, `FUN_392f7890` | The data handler stores the full record before the rate handlers can use it. |
| `FUN_391dfcb0`, `FUN_392f35d0` | G2C `0x20` updates a UInt16 zone's Int32 tax rate. An unknown zone has no effect. |
| `FUN_391dfd30`, `FUN_392f39b0` | G2C `0x21` expects a full dominion record. It can dereference missing state. |
| `FUN_394d7230`, `FUN_397b0ca0`, `FUN_397b9fa0` | C2G `0x08d` contains a 3-byte object ID and Int32 craft payment amount. |
| `FUN_392f42f0`, `FUN_397ab4d0`, `FUN_397b9260` | C2G `0x013` contains a UInt16 zone and Int32 national tax rate. |

`FUN_392f4820` uses a per-mille rate and rejects values above 1000.
It also checks faction authority and a 24-hour change interval.
The earlier value appears in its confirmation dialog. It is not an extra request field.
Dialog type `0x28` is not the network opcode.

The current packet fields for full dominion data match the native field order and widths.
Some semantic names remain unknown, especially the two territory IDs and the participant list fields.
The unclaimed record sends 0 for these fields. No active claim depends on an inferred meaning.

## Limits and next work

Issue 143 permits voting and elections as later work.
The minimum state now includes authored territories, login data, persistent zero ownership, and a durable balance store.
It does not create a working castle economy or award ownership.

Issue 139 still has a client presentation gap.
The exact client contains both craft and national tax requests.
The server now recognizes them and returns an unavailable message.
The craft payment control has no confirmed feature flag.
A client-content change must remove or disable that control before the client can meet the full hidden-UI alternative.
No active craft settlement, nation owner, or voting contract was invented.

A later claim feature needs these facts:

1. Confirm the claim item, monument target, expedition permission, and ownership transition.
2. Confirm both territory IDs and the dimensions that the full record needs.
3. Save the ownership transition and exact item debit in one transaction.
4. Define which tax sources credit the owner and write durable receipts.
5. Add the confirmed authority and timing rules before an active tax-rate change.

## SQL review and checks

The additive `2026-09-12_aaemu_game_dominion_states.sql` creates one table.
The base schema includes the same table. Startup inserts 6 unclaimed rows on a fresh database.
No update changes expeditions, packs, houses, or current balances.
No live SQL was applied during development.

Unit tests check the authored zone mapping, the complete 260-byte packet body, and the confirmed request widths.
MySQL tests check committed balance reload and rollback of a failed balance update.
Human checks must confirm the unclaimed state on the map and the unavailable messages.
No full in-client validation occurred during this work.
