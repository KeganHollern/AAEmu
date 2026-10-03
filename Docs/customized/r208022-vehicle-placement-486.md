# r208022 vehicle summon placement, cluster issue 486

The server now uses the position and yaw selected by the item summon skill.
It checks the full request before it removes the owner's current vehicle.
This change also covers the land and water surface checks from issue 36.
It does not cover cargo, transfers, raised floors, or general vehicle physics.

## Exact client and evidence

The client revision is `208022`, from `client/history.txt`.
The native source file and runtime dump have the same PE timestamp
`543cb835`, image base `38ff0000`, and image size `01cb0a00`.
The dump uses RVA-aligned file offsets.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `client/bin32/x2game.dll` | 18483712 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | 30085120 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Server compact | 119054336 | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The source baseline is `244d1726e34d550951dcea8edbf01be77b892fbc`.
Task-local decompiles, float bytes, and audit logs are under
`.tools-re/vehicles-486-491-20261002/research/placement/` in the cluster workspace.
No extracted client content belongs in this source repository.

## Confirmed route and target contract

The compact contains 78 raw `item_summon_slaves` mappings.
All mappings use `VehicleModel` or `ShipModel`.
The 48 mappings with an item and a use skill use skills `15802`, `19622`, or `22267`.
Each skill uses target kind `SummonPos` and one `SpawnSlave` effect.
These skills have no labor charge, item reagent, product, or later effect.

Native item action `394eaac0` calls the gate at `39349f00`.
Models of type 3 and 4 use the skill route, not the manual SlaveLocator route.
The target producer at `3942c0e0` calls `3934a340`, `39349a10`, and `39347fb0`.
It stores the selected point and yaw in a type-1 position target.
The initialization at `39007590` provides a second check of this target layout.

`CSStartSkill` is C2G level 1, opcode `0x052`.
Its native serializer at `397cad20` writes the skill ID, caster, target, and skill object in that order.
The target serializer at `397bfa90` writes this 31-byte type-1 segment:

| Segment offset | Type | Meaning |
| ---: | --- | --- |
| 0 | `u8` | Target type, `1` |
| 1 | `i64` | Packed world X |
| 9 | `i64` | Packed world Y |
| 17 | `f32` | World Z, metres |
| 21 | `f32` | Yaw, radians |
| 25 | 3-byte object ID | First relative object ID, zero for this producer |
| 28 | 3-byte object ID | Second relative object ID, zero for this producer |

Native helpers `391b8780` and `397b25d0` confirm the 3-byte object IDs.
The target segment is followed by the skill object in the full packet.
This change does not alter that packet reader or its framing.

Native `393459b0` and `39347b30` send `CSSpawnSlave`, opcode `0x02e`.
These functions belong to the manual locator route.
No current mapped summon item uses that route, so its server stub remains unchanged.

The server formerly discarded the position in two places.
`Skill.ApplyEffectsCore` converted the position target to a unit target.
Then `SpawnSlave` ignored the target and used the manager's fallback placement.
The correction preserves the position only for `SummonPos` plus the `SpawnSlave` special effect.
Other effects keep their current target routing.

## Confirmed placement rules

`39347fb0` checks an inclusive 80-metre distance in three dimensions.
The squared constant at `39ac740c` is `6400.0`, bytes `0000c845`.
This limit is separate from `spawn_valid_area_range`.

The land route reads terrain elevation through `I3DEngine + 0x1fc`.
It applies no pivot or foot offset to the selected Z.
The terrain can be at most 10 metres above the owner.
The float at `3999cce8` is `10.0`, bytes `00002041`.
Lower terrain still needs the range and line checks.

The ship route reads water level through `I3DEngine + 0x110`.
It only selects a point when terrain elevation is at or below that water level.
The full hull overlap check includes terrain, so this is not a fixed minimum-depth rule.
The water query uses the shared authored ocean, river, volume, and prefab data.

The line query starts 1 metre above the owner and ends at the selected point.
Its entity mask is `0x107`, for static, sleeping, rigid, and terrain physics.
Living actors do not block this line query.
Its native query flags are `0x8f`.
`393bcb90` and `393bc6b0` collect the caster's attached unit physics for the line's skip list.
The server skips that object's geometry for the line only.
It still includes that object and living actors in the overlap query.

`39347e20` supplies the model bounds to `39180d50`.
The latter function uses the full model half-size, `(max - min) / 2`.
Its narrow box center is the selected position plus the unrotated local bounds center.
The box orientation uses the selected yaw.
The overlap masks are `0x0f` for land and `0x10f` for ships.
These are not the reduced boxes used by the manual locator or shipyard preview.

The client starts from authored offsets and searches nearby candidate points.
The server checks the transmitted result rather than repeat that search.
It does not add the old fixed forward offset to the selected point.
An explicit world origin remains a real position, not a missing-position marker.

## Confirmed saved scroll range

The compact loader at `396d2570` reads `spawn_valid_area_range` into template offset `+0x6c`.
The getter at `39696380` returns that template.
At `39349f00`, the item gate compares this value with the owner's XY distance from the scroll's saved XY.
It only makes this check when the owner has no active local vehicle.
The limit is inclusive and ignores height.
It is not a distance from the owner to the new preview point.

The native saved-location predicate needs both packed coordinate values to be nonzero.
`3961de30` converts the packed pair to a position.
The server preserves the native predicate, including a partially unset pair.

The summon scroll detail body remains 29 bytes:

| Offset | Type | Meaning |
| ---: | --- | --- |
| 0 | `u8` | Slave type |
| 1 | 3-byte ID | Saved slave database ID |
| 4 | `u8` | Destroyed state |
| 5 | `i64` | Repair start time |
| 13 | `i64` | Saved packed X |
| 21 | `i64` | Saved packed Y |

The old implementation treated the final 16 bytes as opaque storage.
It also kept `SummonLocation` in a separate property that did not reach those bytes.
The correction reads and writes that property through the confirmed packed pair.
No SQL migration or compact change is needed.

Successful player removal, visibility removal, cleanup, and death clear the saved pair.
Rejected removal keeps the pair and the other detail bytes.
A failed removal save also keeps the previous state.

The current shutdown path saves an active vehicle's scroll but does not restore that live vehicle after restart.
So its saved location still constrains the next summon after restart.
The owner must return within the authored saved range.
This follows the native gate and the current server lifecycle.
A vehicle restoration system is outside this change.

## Server decisions and limits

These checks are server decisions, not extra native gameplay limits:

- The server rejects nonfinite values, relative targets, unavailable geometry, and a missing land surface.
- Surface comparison allows `0.01` metre for floating-point and packed-coordinate differences.
- A terrain hit within that same tolerance of the line endpoint is the selected surface, not a preceding obstruction.
- Dynamic geometry uses the owner's current visibility neighborhood, which supplies the objects sent to the client.
- Static world geometry still uses its full spatial index.
- Dynamic candidates use their actual model bounds, not `Unit.ModelSize`, which is zero for vehicles.
- A dynamic model without collision parts blocks only queries that intersect its known bounds.
- Unresolved collision animation also prevents a clear result inside the relevant bounds.

The last two rules avoid a false empty-space result when native runtime physics is unavailable.
They do not substitute an invented collision shape.
The exact-client asset audit identifies obsolete mappings with missing content separately from active item routes.

The audit checked 50 distinct models from all 78 raw item mappings.
The 48 active item routes use 33 distinct models.
Of these, 32 models have finite authored bounds.
Model `1427`, the Red Bull car from item `28773` and slave `121`, names a missing prefab entry.
The compact names `speedcar.speedcar_body_redbull` in `game/prefabs/speedcar.xml`.
The exact archive contains only `speedcar.speedcar_side_redbull` for that variant.
The server rejects that unavailable model before any replacement removal.
The test records this exact content gap and rejects any extra unavailable active model.
No other car model supplies substitute bounds.

Models `1249`, `1287`, and `1482` have collision animation that needs a runtime pose.
Their bounds still support their own placement preview check.
As nearby obstacles, they can reject a query through those bounds until the server can resolve that pose.
This is a bounded limitation of the current geometry query, not a failure to summon every cart or ship.
Obsolete model `3` also names missing physical content and has no collision parts.
Its available placeholder bounds limit the unresolved area when that legacy object exists in the world.

The server checks source and destination zone restrictions, ownership, and the source item before geometry.
It finishes placement checks before replacement removal, ID allocation, scroll mutation, or spawn packets.
The existing removal guards still reject combat, cargo, visibility, destroyed, and repair states.
Server-authored launches, including completed shipyards, keep their separate placement path.

## Validation and pending client checks

Automated tests cover the native target segment, numeric boundaries, saved scroll bytes, water and land surfaces, and collision masks.
They also cover the actual `Skill.ApplyEffects` route, local geometry scope, missing collision, and removal side effects.
MySQL tests cover saved scroll coordinates, rejected placement, and accepted removal through the real manager path.
The optional asset test uses `AAEMU_HOUSING_GAME_PAK` and `AAEMU_HOUSING_COMPACT` for exact-client model bounds.

Human validation is still pending:

1. Summon a cart on clear land and check its selected position and direction.
2. Summon a boat in deep water, then try shallow water and an occupied point.
3. Try a blocked placement while another vehicle is active and check that the current vehicle remains.
4. Remove a vehicle, move to another valid area, and summon it again.
5. After a restart with an active scroll, check the authored saved-location restriction and its message.

Keep issue 36 open for its broader surface, transfer, cargo, and physics work.

[The separate content issue #589](https://github.com/KeganHollern/aaemu-cluster/issues/589) tracks the missing Red Bull body prefab.
