# r208022 mount mileage and riding XP

This record supports [cluster issue #490](https://github.com/KeganHollern/aaemu-cluster/issues/490).
The source base is `7802607b7f0029042956a78d9b5b58aff7aa8b6b`.
The application base is `b10eb50440fa309fad262c8b54541fa79bcd5364`.
The review date is 2026-10-08.
The user approved the custom XP rule on 2026-10-08.
This change needs no client release, database schema change, or compact edit.

## Input identity

| Input | SHA-256 |
| --- | --- |
| Packed r208022 `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The PE image base is `0x38ff0000`, image size is `0x01cb0a00`, and timestamp is `0x543cb835`.
Native addresses below refer to the runtime dump.
This review used an earlier local capture and did not run a client experiment.
The client and server compacts are separate inputs with different schemas.

## Confirmed client contract

`SCMileageChangedPacket` uses game packet level 1 and opcode `0x101`.
Its body contains a packed unsigned object ID, followed by a signed 32-bit mileage delta.
The body contains no other fields.
The current server packet layout agrees with the native serializer.

| Native address | Evidence |
| --- | --- |
| `0x391aee60` | The packet factory sets opcode `0x101`. |
| `0x397c66b0` | The serializer writes packed `unitId`, then signed integer `mileage`. |
| `0x391d9ea0` | `OnMileageChanged` resolves the object and passes the signed change to the consumer. |
| `0x39357390` | The consumer ignores a zero change and emits `MILEAGE_CHANGED`. |
| `0x3933a910` | The consumer adds the delta to the locally owned mate's stored mileage. |

The native update is `mateInfo.mileage += delta` at mate-info offset `0x40`.
The server must send a change, not the cumulative total, in `SCMileageChangedPacket`.
Repeated totals would make the client overcount mileage.

`SCMateSpawnedPacket`, opcode `0x125`, instead supplies the total mileage.
Native serializer `0x3989a910` places that signed integer after the signed XP field.
`OnMateSpawned` at `0x391db180` passes the spawn state through `0x3933fd00` to initializer `0x3933ac10`.
The initializer writes the total directly to mate-info offset `0x40`.
The existing server spawn packet agrees with that field position.

The `GetPetMileage` registration at `0x3944847b` selects getter `0x39442670`.
That getter returns the mileage field as a Lua number without a conversion.
These paths prove the integer counter and its update rules.
They do not prove a distance unit, XP rate, or award interval.

## Client data and UI findings

Both compacts contain the same 101 rows in `levels`.
`levels.total_mate_exp` defines cumulative XP thresholds for mate levels.
It does not define earned XP per metre, per second, or per level.
The active server level cap determines which thresholds a mate can reach.

The review checked both schemas and the complete relevant config and formula tables.
Those tables were `constants`, `content_configs`, `formulas`, `unit_formulas`, `unit_formula_variables`, and `world_spec_configs`.
No named field defines a mileage unit or riding XP award.
The numeric IDs in `constants` and `content_configs` need a native reference before they can establish a rule.
Generic formulas 8 and 9 concern lost and recoverable death XP.
They do not establish a riding XP rate.

The script scan covered 1,434 payloads in the exact local `game_pak`.
No reviewed script calls `GetPetMileage` or defines a riding XP rate.
The only `MILEAGE_CHANGED` listener is in `game/scriptsbin/x2ui/combattext/combat_text.alb`.
Its callback at source lines 652–654 contains only a return instruction.

The pet action bar reads XP through `X2:GetPetExpToNextLevel`.
Its `GetPetExpString` function, at source lines 89–96, formats the current progress and next-level requirement.
Its XP-bar function, at source lines 681–690, uses the same values.
`EXP_CHANGED` and `LEVEL_CHANGED` update that display.
Neither function converts movement or mileage into XP.

The reviewed inputs do not establish the retail server's riding XP calculation.
An unconfirmed numeric match is not evidence of that calculation.
The implementation must identify any chosen rate as a server rule.

## Defects at the reviewed source base

`MateXpUpdateTask` fixes the award at 300 XP and sends a debug chat message to the owner.
`Mate.StartUpdateXp` schedules the task after 60 seconds.
That task does not measure the distance travelled during the interval.
`Mate.AddExp` applies the existing `World.ExpRate` multiplier to the award.

The `Mileage` assignments only initialize, load, restore, copy, and save the existing counter.
No movement path increases that counter.
The database already stores mileage in `mates.mileage`, and the spawn packet already sends it.
Those storage and packet fields do not supply the missing movement calculation.

## Mileage behavior

The server counts horizontal distance from accepted movement packets.
The current owner must occupy the driver seat of an active, persistent mount.
The owner and mount must be alive and in the same world instance.
Temporary summons, passengers, unmounted pets, and replaced mount objects receive no mileage.
Carried movement and skill-controller movement receive no mileage.

The first eligible packet establishes a position and time baseline.
After a baseline reset, the first packet also establishes a new baseline without an award.
A position change outside this packet path breaks continuity.
The next packet then establishes a baseline instead of counting that change.
Each accepted segment contributes its horizontal length, with fractional metres retained during the same summon.
Stationary packets and vertical-only movement add no distance.

The distance allowance uses the existing movement limits of 30 metres per second and 100 metres per packet.
One shared allowance permits an initial 3-metre burst.
Elapsed time restores that allowance, up to 100 metres.
Packet frequency does not grant a new allowance for every packet.
An oversized segment adds no mileage, even when the movement guard tolerates it.

Whole metres update both `Mate.Mileage` and its owner's matching `MateDb` under the persistence lock.
The normal character save transaction writes the total to SQL.
Movement does not start a separate SQL transaction.
The total stops at `int.MaxValue`.
The client receives only the applied change through `SCMileageChangedPacket`.

Stops retain fractional metres.
Driver seat changes and injury clear the movement baseline but retain fractions during the same summon.
A new summon creates a new tracker.
Resummon, reconnect, and server restart retain the saved whole metres but discard less than 1 metre of unsaved fraction.
The existing database schema and spawn packet already support the whole-metre total.

## Approved server XP rule

The user approved the following server rule:

- The mileage counter records whole metres of valid mounted movement.
- Every 2 metres give 1 base XP before the existing `World.ExpRate` multiplier.
- The riding award has no extra multiplier for the mount's level.
- The existing authored mate XP thresholds remain unchanged.

This is a custom server rule, not a confirmed retail formula.
[Research issue #683](https://github.com/KeganHollern/aaemu-cluster/issues/683) tracks the missing retail evidence.
For each whole-metre update, the server uses the current rate at both interval endpoints:

```text
old_total = mileage before the update
new_total = mileage after the update
rate = current World.ExpRate
XP award = floor(new_total * rate / 2) - floor(old_total * rate / 2)
```

The server applies the rate once through this calculation.
It does not apply `World.ExpRate` a second time to the result.
At rate 1, consecutive 1-metre updates give 0 XP, then 1 XP.
An odd saved mileage total retains that half-XP credit through resummon, reconnect, and restart.
At a fixed rate, a route gives the same XP regardless of packet frequency.
The total also retains fractional XP credit from non-integer rates without a separate stored remainder.

A rate change applies the current rate only to the next mileage interval.
The server uses that current rate for both endpoints, so a rate increase gives no retroactive award for earlier distance.
The server never reconstructs or replaces the mount's existing XP from its mileage total.
The normal mount level cap still limits XP progression.

The fixed 60-second award and its debug chat message are removed.
Stationary time alone gives no riding XP.
Combat XP uses its separate existing path.

## Human checks

Manual checks belong to [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
The release record supplies the exact release and source commit for these planned checks.
The checks need one owned persistent mount below the level cap and the current `World.ExpRate` value.
The server can supply read-only mileage totals because the default client UI does not show them.

1. Record the mount's XP and mileage, then ride a measured route after the first movement establishes the baseline.
   Check the whole-metre change and the XP interval formula.
2. Stay mounted without movement for more than 60 seconds.
   Check that neither mileage nor XP changes and that no old debug chat message appears.
3. Dismount, move on foot, then mount again.
   Check that foot travel gives no mileage or riding XP and that the next ride uses a new baseline.
4. Dismiss and resummon the mount, then repeat the check after reconnect.
   Check that whole mileage and XP remain correct and that no award repeats from earlier distance.
5. Check a server teleport or world transition during a mounted session.
   Check that the position change gives no riding mileage or XP and that normal travel resumes from a new baseline.

Automated tests and service readiness do not complete those checks.
