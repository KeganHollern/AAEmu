# Server-owned subzone discovery (#524)

The server uses the character's current world, instance, and position to find containing subzone polygons.
`CSNotifySubZonePacket` retains its current 4-byte unsigned ID body.
The client ID is only a hint among the containing polygons.
A forged or stale hint cannot select an area outside the server position.
If the hint is invalid, the server keeps the current containing area or selects the lowest containing ID.
If no polygon contains the position, the server clears `SubZoneId` to 0.
An absent world, stale instance, or non-finite position grants no destination.

The visit path adds one district and sends one book update for a new visit.
The normal character save persists that visit.
Repeated notifications do not add a duplicate visit or send another book update.
The visit mutation uses the same persistence lock as the character save.
This change does not remove earlier stored visits or change the database schema.

## Geometry corrections

The previous polygon test used X=1000 as the endpoint of a ray.
That finite segment gave incorrect results at ordinary world coordinates.
The new test uses the exact client's half-open ray crossings.
The query checks all polygons in the current world because a polygon can cross a terrain zone boundary.

The parser reads Group 18 and uses Area.Id as the subzone ID.
It applies the zone origin, cell offsets, entity position, quaternion rotation, and scale.
It removes exact duplicates, including Height in that comparison.
The parser retains authored ID 0 shapes, but lookup excludes that ID from membership.
The vector query checks the authored height volume.
The explicit X/Y query remains a 2D lookup.

The extracted r208022 data contains 115 files and 1,047 authored shapes.
The main world has 940 shapes, including 39 rotated shapes and 33 scaled shapes.
Recall subzones 641 and 158 have positive heights of 50 and 150.
The previous XY-only query could accept a character above or below those volumes.

## Client identity

- `client/history.txt` states `version 208022`.
- Input: `/home/kegan/archeage/client/bin32/cryentitysystem.dll`.
- Size: `944640` bytes.
- SHA-256: `d1f0623a45c491888b9a2f3de54270d142d17afe2a63dd4c4ae95a3ea2a7377e`.
- PE timestamp: `0x543cb80b`.
- PE image base: `0x32000000`.
- PE SizeOfImage: `0x00101000`.
- This review used the installed DLL directly. It did not mix an unpacked or relocated runtime dump with this input.

## Confirmed native address chain

1. `FUN_32084070` loads the Area XML fields. It stores `Group` at area offset `0x44`. It stores raw `Height` at offset `0x100`.
2. `FUN_32083a10` applies the entity matrix to each point. This includes rotation, scale, and translation. It passes those transformed points to `FUN_32094ad0`.
3. `FUN_32094ad0` stores the minimum transformed point Z at offset `0xfc`.
4. `FUN_3208f090` checks polygon containment. When `ignoreHeight=false` and Height is positive, it rejects `Z < minimumZ` and `Z > minimumZ + Height`.
5. The comparison constant at `0x320c3370` is float `0`. `objdump -s` independently confirmed its bytes as `00000000`.
6. `CAreaManager::UpdatePlayer`, `FUN_3209bfd0`, calls the predicate with `ignoreHeight=false` at `0x3209c367`.
7. Its area enter/leave path, `FUN_32099ed0`, also calls the predicate with `ignoreHeight=false`. It places the authored Group value in the area event. Group 18 has no exception that bypasses height.
8. The housing query at `FUN_32098a30` instead selects Group 1 and passes `ignoreHeight=true`. That housing-specific rule does not apply to player subzone entry.

Confirmed polygon rules:

- Positive Height defines an inclusive interval `[minimum transformed point Z, minimum transformed point Z + Height]`.
- Height 0 has no vertical limit.
- Height remains the raw authored value. The native loader does not multiply it by entity scale.
- XY uses half-open ray crossings. Horizontal edges do not cross. Vertical edges include equality in X.
- The native player-area path supports height-sensitive subzone membership. This evidence does not establish a new wire layout for `CSNotifySubZonePacket`.

## Automated checks

The focused tests cover translated and concave polygons, terrain boundaries, overlap, and each height boundary.
They also cover raw unscaled Height, unbounded Height 0, zero IDs, and all extracted client shapes.
Packet tests reject remote hints, missing worlds, stale instances, and non-finite positions.
A real MySQL test saves and reloads the book before and after a real district entry.
It confirms that a forged remote hint does not persist the remote destination.
No live player data changed during these tests.

## Pending human checks for HUMAN VALIDATION #573

- [ ] HV524-1. Enter an unvisited recall district. Check that the book adds the correct destination once.
- [ ] HV524-2. Leave and enter that district again. Check that the book has no duplicate destination.
- [ ] HV524-3. Reconnect after the visit. Check that the destination remains available and works through a normal portal.
- [ ] HV524-4. Cross a district boundary on foot. Check that the region display and new recall destination match the actual district.
- [ ] HV524-5. On a prepared test character, enter recall subzone 641 or 158 at its normal height. Check the destination unlock. A separate fresh test character above or below its volume must not unlock it.

The height check needs a controlled test character and exact position setup.
Do not remove Kegan's stored visits to prepare that test.
Human validation remains pending.
