# House decoration limits and public farms

This record covers cluster issues [#497](https://github.com/KeganHollern/aaemu-cluster/issues/497)
and [#500](https://github.com/KeganHollern/aaemu-cluster/issues/500).
The research date is 2026-09-12. The source base is
`c73605275cb8683213ce331897c7540687e161a3`.

## Exact client evidence

`client/history.txt` identifies ArcheAge r208022. Native addresses in this
record use the loaded image base `0x38ff0000`.

| Binary | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `.tools-re/dumps/crynetwork.dumped.dll` | `5115de707d457a85bd5ea95d78b6e069cba9d1c3f7b7b82dd41c8188995dbe11` |

The original and dumped X2Game images share the PE timestamp, image base,
entry point, and image size. The PE timestamp is 2014-10-14. The entry point
is `0x8c5d6d`. The image size is `0x1cb0a00`.
The cluster workspace retains native functions, X2UI disassembly, and data
queries under `.tools-re/housing-20260912/limits-farms/`.
The checked `compact/server.sqlite3` SHA-256 is
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
No client content changes are part of these fixes.

## Decoration rules

The server loads all 12 `housing_deco_limits`, 23 `housing_deco_limit_elems`,
and 6 `deco_actability_groups` rows. It checks capacity before decoration
creation. An incomplete house returns error 123.

The exact native rules refine the static audit in #497:

| Rule | Confirmed native evidence | Server result |
| --- | --- | --- |
| `absolute_deco_limit` counts all house doodads, including bound objects. | `39327c50`, descriptor loader `39677360` | Error 124 when full |
| `deco_limit` counts only furniture whose source item has `Restore=true`. | `39327c50`, item lookup `396a6e00` | Error 200 for another restorable item when full |
| Category limits use `housing_deco_limit_id` and `deco_actability_group_id`. | `39327bc0` | Error 629 when full or absent |
| Category 5 counts each storage coffer. Other categories count distinct source item types. | `39897400`, `398977e0`, special group constant `39acb6e8=5` | Duplicate coffers consume capacity |
| Proficiency bonuses use distinct source item types within the shared category limit. | `398978a0`, `3947a040`, `39479890` | Duplicate or excess furniture cannot increase the bonus |

Category 0 has no category cap. A full category also rejects another instance
of a type already present. Native `39327bc0` checks the current category count
before placement. It does not subtract duplicates from that check.
Native `394796b0` subtracts bound objects from the total and absolute limit
for the displayed furniture count.

The server uses the source item ID for category and bonus counts. For old
doodads without that ID, it uses the loaded decoration design relation.
It orders placed furniture by database ID, then object ID. This makes bonus
selection repeatable when old house contents exceed a category cap.
It gives no house bonus while construction is incomplete.

## Public farm protection and data

The server compares the current UTC time with the end of the protection
window. Protection ends at the exact expiry time, before the next cleanup
tick. A cleanup pass and each public crop list use one time snapshot.
The persistence lock protects those snapshots and the ownership changes.

The server loads all 17 `common_farms` rows. Their `guard_time` values use
milliseconds. All current farm groups use `86400000` milliseconds, or 24 hours.
Native `394445b0` returns the table value. The common farm X2UI divides this
value by 1000 before it formats the time. `doodad_groups.guard_on_field_time`
uses seconds and does not define public farm protection.

The subzone association is an **inferred server data relation**. It is not a
native foreign key. The authored `common_farms.name` values match the authored
`sub_zones.name` values. This relation gives the same 5 subzones as the old
hard-coded list:

| Subzone ID | Farm group |
| --- | --- |
| 966 | Farm |
| 998 | Farm |
| 967 | Ranch |
| 968 | Nursery |
| 974 | Stable |

The server uses the current subzone geometry. It rejects ambiguous name
matches and different guard times within one farm group. The 8 extracted
`common_farm.xml` files contain empty object lists. The `common_farms` comments
do not supply reliable geometry. Several comments disagree with current
subzone locations. The server does not use those comments or make new farm
locations.

The `doodad_groups` loader reads `removed_by_house` and `is_export` as `t/f`
text booleans. `IsRemovedByHouse` exposes the authored crop removal rule for
house placement in #499.

## Corrected info board acceptance

The r208022 info board opens its window locally. Native `393b8700` raises
`OPEN_COMMON_FARM_INFO` with the authored `DoodadFuncOpenFarmInfo.FarmId`.
Registration `39214380` identifies this event as `0x102`.
The X2UI `commonfarm/info.alb` handler calls `X2:GetCommonFarmInfo`, whose native
function is `394445b0`. It reads the farm name, protection time, group capacity,
description, and allowed crops from client compact data.
This window does not read a current planted count from a server packet.

The 4 authored board functions use skill `16968`, farm IDs `1` through `4`,
and `next_phase=-1`. The server keeps the board phase. It does not treat this
local UI action as a request to delete the board.

The static audit asks for `SCShowCommonFarmPacket` from the board. The exact
client contradicts that proposed lifecycle. Its handler updates the map marker
cache and does not open the info board. The server sends no new board packet.

## SCShowCommonFarmPacket contract

The dormant writer now matches the confirmed r208022 body. Its opcode is
`0x1b0`, level 1. Registration `391ca5a0`, factory `391b5190`, parser `397b7990`,
and handler `39182220` identify this packet independently.

| Body field | Type | Meaning |
| --- | --- | --- |
| Farm ID | `uint32` | A `common_farms.id` key |
| Count | `int32` | Number of positions, at most 128 |
| Positions | 9 bytes each | Packed X, Y, and Z positions |

Parser `3961e2c0` reads each packed position. The packet factory allocates
`0xc18` bytes. The native parser limits the list to 128 positions.
Consumer `3902f940` appends the positions to the map marker cache.
The server writer uses `PacketStream.WritePosition` and applies the count cap
before serialization. The body length is `8 + 9 * count` bytes.

AAEmu has no proven independent store of these farm marker IDs and positions.
The corrected writer stays unsent. It does not manufacture entries from
farm groups, comments, notice boards, or arbitrary representative farm IDs.
The crop list packet `SCResponseCommonFarmListPacket` and private farm notice
packet `SCHouseFarmPacket` have separate lifecycles. This change does not add
new sends for either packet.

## Validation

The unit tests cover unfinished houses, absolute capacity, restorable capacity,
category capacity, duplicate storage, and shared proficiency bonus limits.
They cover protection expiry, the local board action, data ambiguity, and the
complete 17-row farm data set. The real compact test checks all 23 category
limits and all 5 subzone relations. It also checks both crop removal states.
Packet tests read every field for empty, two-position, and over-capacity lists.
Each packet test checks that no bytes remain.

The Release build passed with 0 errors and 66 current warnings. All 3252 unit
tests passed with 0 skipped tests. The test run used the real compact and
the housing area fixture through `AAEMU_HOUSING_COMPACT` and
`AAEMU_HOUSING_CLIENT_ROOT`. These checks do not constitute a manual client test.

Do these focused checks with r208022 after deployment:

1. Try decoration placement before and after house construction completes.
2. Fill a storage category and try to place another coffer.
3. Compare the client and server proficiency bonuses for duplicate specialty furniture.
4. Open each public farm board twice and make sure the board remains present.
5. Check crop access before and after the authored 24-hour protection window.

The full map marker lifecycle remains separate packet research under #149.
