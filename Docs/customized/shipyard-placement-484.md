# Shipyard placement for r208022

This change resolves cluster issue #484.
The server checks a shipyard placement before payment or object-ID allocation.
The related [persistence change](shipyard-persistence-485.md) resolves issue #485.

## Client identity and evidence

The local `client/history.txt` identifies revision 208022.
The native analysis used the existing `x2game.dumped.dll` dump.
Its SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The file has 30,085,120 bytes, PE timestamp 1413265461, base `0x38ff0000`, entry RVA `0x8c5d6d`, and image size `0x1cb0a00`.
Ghidra 12.1.3 read the task's separate copy of the existing analysis project.
This task did not capture a new runtime dump or a live packet.

The ignored evidence is in `.tools-re/shipyards-484-485-20261002/research/placement/` in the cluster workspace.
That directory contains the decompiled functions and `native-identity-constants.json`.
The SHA-256 of `DecompileShipyardPlacement.java` is `9b812759bc0f71cb52846f2dee1214eebc050333c8664520bf8cab57fcd6e8c2`.
The packet history starts with `6c0428d45` and the offset conversion in `44d5db59a`.
The pre-change source at `0b48434c4a78eb408be120c1413528e62808aa77` discarded the bounds and accepted the requested position.

## Confirmed packet contract

`CSCreateShipyard` is a C2G packet at level 1, opcode `0x0fc`.
The fixed body has 61 bytes.
The native producer `394d3fb0`, constructor `397aca10`, serializer `397bc060`, and coordinate serializer `3961e270` agree with the AAEmu reader.
There is no variable group or optional trailing field.
The reader rejects a truncated body or extra body bytes before it changes state.

| Offset | Type | Field |
| ---: | --- | --- |
| 0 | u32 | Shipyard template ID. |
| 4 | i64 | Packed world X. |
| 12 | i64 | Packed world Y. |
| 20 | f32 | World Z in metres. |
| 24 | f32 | Yaw in radians. |
| 28 | u64 | Exact design item ID. |
| 36 | 3 f32 | Model-local minimum X, Y, and Z. |
| 48 | 3 f32 | Model-local maximum X, Y, and Z. |
| 60 | bool | `autoUseAAPoint`. |

`394d2b50` gets the preview entity's local bounds before the producer copies those bounds into the packet.
`394d27c0` sets a yaw-only preview rotation.
`394d8600` and `394d5140` select the first construction model for the preview.
The server loads that model from the authored step 0.
It parses the claimed bounds but never uses them as collision authority.
The server retains the current gold and item payment path for `autoUseAAPoint`.

## Confirmed native geometry

`394d73e0` uses the cursor ray result and adds 9.9 metres to Z.
`394298c0` includes water in that ray query.
`394d5d90` rejects a squared 3D owner distance greater than 2025.
Thus, 45 metres is inclusive and does not depend on `build_radius`.

The native validator uses 3 different boxes.
Each box uses the preview yaw, but their sizes and centres differ.

| Query | Local centre | Half-size | Native code |
| --- | --- | --- | --- |
| Other shipyards | `(min + max) * 0.5 + (0,15,0)` | `(max - min) * 0.5 + (build_radius,15,0)` | `394d2860`, `394d41a0`. |
| Living units | `(min + max) * 0.5 + (0,15,0)` | `(max - min) * 0.5` | `394d1750`. |
| Water and terrain clearance | `(min + max) * 0.3 + (0,15,0)` | `(max - min) * 0.3 + (build_radius,15,1.3)` | `394d5d90`. |

The other-shipyard query rotates each centre before it adds the world position.
The neighbor box adds 15 metres along local Y but does not add the candidate's build radius.
`390318f0` uses the full separating-axis test and treats touching faces as overlap.
The living query adds its local centre directly to the world position, as the native helper does.
Its entity mask is `8`.
The final clearance query rotates its centre and uses entity mask `0x10d`, which includes terrain.

The raw native constants are:

| Address | Bytes | Float |
| --- | --- | ---: |
| `3999c47c` | `0000003f` | 0.5 |
| `3999ccd0` | `9a99993e` | 0.3 |
| `399e578c` | `00007041` | 15 |
| `399e5774` | `6666a63f` | 1.3 |
| `399e5918` | `66661e41` | 9.9 |
| `399e58b8` | `0020fd44` | 2025 |

## Server checks and inferences

The exact design must exist in the owner's bag with the expected template and count.
The compact `item_shipyards` row must map that item template to the requested shipyard.
`origin_item_id` alone is insufficient.
For example, templates 14 and 15 both name origin item 28013, but `item_shipyards` maps it only to 15.
Legacy mappings whose template has no matching origin design remain unavailable through this payment path.

The server uses the owner's current world and checks both source and destination zone bans through `ZoneSkillRestrictions`.
It also rejects a missing or closed destination zone.
Native `394d5d90` checks the closed flag at offset `+4` of the destination zone record.
Loader `3982c0f0` reads `zones.closed` from SQL column 1 and stores its Boolean value at that exact offset.
The loader confirms this check independently of the existing server field name.

The server checks the water surface at the requested XY and the height of the ground below that point.
The requested Z must match water plus 9.9 metres within 0.01 metre.
This is a server check derived from the native preview path, with a float tolerance at world coordinates.
It prevents a forged Z from moving the clearance box above land or other objects.
Zero is a valid ocean level.
Missing water, terrain, or model data rejects placement.

The shared geometry resolver supplies houses, doodads, units, static world objects, and terrain.
Terrain checks use the authored grid triangles and their solid columns, including interior peaks.
The terrain-column interpretation prevents a fully buried box from passing a surface-only triangle test.
This solid-column interpretation is a server inference, not a newly recovered CryPhysics algorithm.
Authored holes omit those terrain triangles.
An unavailable terrain sample returns an indeterminate result.

The current world geometry resolver represents non-actor units as static collision objects.
It does not reproduce every native moving-versus-sleeping rigid-body state.
Its conservative overlap result can reject a placement beside a moving vehicle that the native preview permits.
No new exclusion for such vehicles was added without exact ownership and physics evidence.

Placement is available only in the default persistent world instance.
This restriction matches the current persisted doodad loader used for shipyard salvage.
The placement check and payment hold the persistence lock in one operation.
The private creation method prevents another server caller from bypassing placement checks.

## Tests and pending human checks

Packet tests cover all fields, every truncated length, extra bytes, and complete body consumption.
Rule tests cover range boundaries, water height, native box dimensions, rotation, overlap, unknown geometry, and entity masks.
Terrain tests cover depth, a fully buried box, an interior peak, holes, and missing terrain.
Zone tests use the real source and destination ban checks and check rejection before design consumption.
The payment tests check unchanged assets and no allocated IDs for rejected placement.
The optional exact-client test loads the mapped step-0 models from the r208022 archive and checks their bounds.

Human validation remains necessary for preview agreement at a clear shore, a shallow shore, and beside another shipyard or house.
The shared HUMAN VALIDATION issue records these checks after release.
This task does not claim live-client validation.
