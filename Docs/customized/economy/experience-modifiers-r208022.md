# Experience modifiers and the premium point field

This change resolves cluster issue 476 and completes the premium display correction for issue 462.

## Exact data

The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The client compact SHA-256 is `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.

`unit_modifiers` has 46 rows for attributes 95 and 186.
These rows include 45 buff rows and 1 NPC row.

| Data | Meaning |
| --- | --- |
| Buff 3084, modifier 21495, attribute 95, value 20 | Add 20 percent XP |
| Buff 8000011, modifier 8000065, attribute 95, value 10 | Add 10 percent XP during patron entitlement |
| Buff 8213, modifier 33058, attribute 186, value 100 | Add 100 percent labor XP |
| Buff 8296, modifier 33255, attribute 186, value 200 | Add 200 percent labor XP |
| NPC 13444, modifier 28687, attribute 95, value -500 | Remove the NPC XP award through the lower limit |
| `unit_attribute_limits` row 21, attribute 95 | Minimum 0, maximum 500 percent |

The base multiplier is 100 percent. The limit applies to the complete multiplier, including that base.
The current compact has no limit row for attribute 186.
The loader applies a limit to either attribute when its row exists. It does not copy attribute 95 limits to attribute 186.

## Award paths

Normal positive XP uses the world rate and `ExpMul` once.
Labor XP also uses `ExpByLaborPowerMul` once, after the proficiency multiplier.
Auction mail claims use the same calculation before the transaction commits their prepared state.
The staged labor completion path must call `AddLaborExperience` for trade packs and skill costs.
NPC kill XP uses the NPC's `ExpMul` after its template multiplier and adder.
XP totals and ability totals use wider addition before the current maximum caps apply.

Negative XP changes do not receive gain modifiers.
Recovered death XP uses `RestoreExperience`, which bypasses the world rate and gain modifiers.
This rule prevents a scroll from creating extra XP during death recovery.
The priest recovery path already restores the recorded loss directly.

## Native premium field

The exact client image is ArcheAge r208022 `x2game.dll`.
Its source SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The mapped image SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The mapped base is `0x38ff0000`. Its PE timestamp is `2014-10-14T00:44:21Z`.
The mapped image has matching PE fields and internal references. The earlier dump procedure is not recorded.
All addresses below refer to that mapped image.

| Address | Evidence |
| --- | --- |
| `0x397c9480` | The NetUnit serializer reads an i32 named `premium` at byte offset `0x2080`, after visual options and before 6 `pStat` values. |
| `0x3936b000` | `ClientUnit::InitFromNetUnit` calls the NetUnit copy function with `ClientUnit + 8`. |
| `0x398a1f90` | The copy moves `NetUnit[0x820]` to destination `[0xef0]`, which is `ClientUnit + 0x3bc8`. |
| `0x39365ad0` | The display consumer reads `ClientUnit + 0x3bc8`, calls the point-to-grade function, and selects `ui/hud/primium.dds`. |
| `0x397a7d10` | The mapper selects a grade from the point thresholds. |
| `0x391d4050` | The account-info handler logs the same mapping as `premium point` and `premium grade`. |

This field contains premium points. It does not contain a boolean or a grade ID.
`premium_grades` maps point 0 to grade 1 and point 1 to grade 2.
`PremiumGameData` now loads these points with the benefits.
`SCUnitStatePacket` writes point 0 for non-patron state and point 1 for active patron state.
The purchase feature remains disabled under issue 316.

Local research output is in `.tools-re/economy/native-patron-*.log` in the cluster workspace.
These machine files are not release inputs.

## Checks

The tests start a real scroll buff and check a 20 percent award increase.
They check the 0 and 500 percent limits, both labor buffs, combined modifiers, NPC penalty, and large positive input.
They also check removal of the scroll modifier and any authored limit for attribute 186.
The patron tests check both compact point values.

After publication, compare the same XP reward before and after a 20 percent scroll.
Compare the same labor action before and after a labor XP buff.
Check death recovery while an XP scroll is active. Recovery must return only the recorded recoverable XP.
Check the patron display with an active period and with no period.
These client checks do not yet have human validation.
