# Account patron periods

This change resolves cluster issue 462. It also removes the unconditional patron buff from issue 476.

## Account state and admission

Login owns the account entitlement in `users.patron_start` and `users.patron_end`.
Each value uses unsigned UTC Unix seconds. A zero pair means no entitlement.
A nonzero period must have `start < end <= 253402300799`.
Future and expired periods remain valid data. Game treats the start as inclusive and the end as exclusive.

Login reads these values before it creates a Game admission request.
A failed read, missing account, or invalid period rejects admission.
The one-use Game token keeps the account ID and its immutable period together.
Game assigns this period before it calculates offline labor.
A new connection has no patron entitlement by default.

The internal `LGPlayerEnter` body now has 24 bytes:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | u32, little endian | Account ID |
| 4 | u32, little endian | Connection ID |
| 8 | u64, little endian | Patron start |
| 16 | u64, little endian | Patron end |

Both servers use this contract. Game rejects old 8-byte bodies and extra bytes.
The Game and Login tests share this exact fixture:
`2A0000007856341200F153650000000000D2496B00000000`.
It means account 42, connection `0x12345678`, start 1700000000, and end 1800000000.

An account-date change takes effect at the next Game admission.
The active connection checks its stored dates against UTC time.
Buff 8000011 applies only during that period. The periodic task adds or removes it within 1 minute of a date boundary.
The client receives the account dates through the current `SCAccountInfoPacket`.

## Exact compact data

The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The client compact SHA-256 is `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
`PremiumGameData` reads `premium_benefits` and `premium_grades` at startup.

| Grade | State | Online labor | Offline labor | Labor cap |
| --- | --- | --- | --- | --- |
| 1 | Non-patron | 5 | 0 | 2000 |
| 2 | Patron | 10 | 5 | 5000 |

The current config uses a 5-minute tick.
Labor amounts and caps come from the compact. The config still supplies the tick intervals.
Only complete ticks in the overlap with the patron period receive the patron rate difference.
The non-patron rate applies to the whole interval.
A login starts the offline calculation at the last recorded labor tick, not the previous login time.
This change prevents another award for time that already received online labor.
The current labor cap limits new awards. A date expiry does not remove labor above the lower cap.

`premium_grades` maps point 0 to grade 1 and point 1 to grade 2.
The NetUnit premium field uses this point value for the active account state.
`premium_configs` describes connect and disconnect points. These tables do not define a purchased account period.
They do not authorize a patron grant or establish a sale price.
The premium purchase route remains disabled under issue 316.
The shipped credit defaults grant 0 credits for both account states.

## Database effects

The paired Login update is `2026-09-12_aaemu_login_patron.sql`.
It adds 2 `BIGINT UNSIGNED NOT NULL DEFAULT 0` columns to `users`.
The update checks for each missing column and preserves any current values.
The base schema includes both columns.
No statement grants patron, changes a balance, or estimates an account period.
The update does not change the compact or Game database schema.
Login applies this reviewed update through its normal startup migration path.

## Checks

The Game tests check default state, date boundaries, labor overlap, malformed admission, token ownership, and one-use admission.
They also check the compact loader, shared packet bytes, and zero credit defaults.
The Login tests check stored values across service replacement, invalid accounts, failed reads, and the admission bytes.
Disposable MySQL tests check fresh schema creation, an upgrade with absent columns, and a retry with current account dates.

After publication, check these cases in the client:

1. Connect with an account that has no patron period. Check the labor cap and the absence of buff 8000011.
2. Use an approved test account with an active period. Check its labor cap, buff, and online and offline labor.
3. Keep that account connected through the end time. Check that the buff clears within 1 minute.
4. Connect again. Check that the expired period does not grant patron benefits or automatic credits.

These human checks need the paired Login and Game release. No live account dates changed during source tests.
