# r208022 housing area rules

This change resolves the construction rules in [cluster issue #496](https://github.com/KeganHollern/aaemu-cluster/issues/496).
It does not change placement collision, tax expiry, tax extension, or the housing-area notice doodad.
Issue #307 retains the wider placement collision work. The build guard binds the design item to its housing template.

## Source and client evidence

The starting fork commit is `c721206cae898b205e86f5512b650d7003f379f6`.
Official `AAEmu/AAEmu:develop` at `00ed43a0` did not add these rules to `HousingManager` or `HousingGameData`.

The server compact remains unchanged:

- Its SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
- It contains 401 `housing_areas`, 15 `housing_groups`, and 32 `housing_group_categories` rows.
- Group 1 permits category 8 with `max_construct_count=3`. A limit of 0 means no category count limit.
- Groups 9 and 10 name categories 17 and 18 in `existing_category_id`.
- Groups 12 and 13 set `houseless`.

The exact client is `r208022`, as recorded in `client/history.txt`.
The native analysis used the current `x2game.dumped.dll` analysis project in read-only mode.
The original DLL SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The dump SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Both files use image base `0x38ff0000`, PE timestamp `0x543cb835`, entry RVA `0x008c5d6d`, and image size `0x01cb0a00`.
The current task reused the earlier dump. It did not create a new dump or change the client.

Native `0x39333e20` queries the housing-area value at the proposed position.
It calls `0x39897280` with that value and the housing category.
That helper gets the area, its group, and its permitted categories. It returns the category limit through an output parameter.
A category mismatch produces error `0x244`, decimal 580.
The area getter is `0x3963b450`, and the group getter is `0x3963b410`.

`existing_category_id` prohibits another building when the account already owns that category.
It does not demand an earlier building, contrary to the initial issue text.
The group descriptions and client `ui_texts` rows 4776 and 4777 support the account-wide prohibition.
Their keys are `HOUSE_CANNOT_OWN_MORE_HOUSELESS_CONDITION` and `HOUSE_CANNOT_OWN_MORE_EXISTING_CATEGORY_CONDITION`.

## Geometry

The current `SubZoneManager` already reads `housing_area.xml` from the mounted `game_pak`.
The old loader used `Area.Id`, which is often 0. The permit identifier is `Area.value1`.
For example, zone 138 entity `LevelDesignShape_138_moang_8` uses `value1=11`, which matches compact area 11.

The new housing parser keeps the current zone and cell conversion:

```text
world point = (zone origin + entity cell) * 1024
            + entity position
            + rotated and scaled local point
```

Only X and Y receive the cell offset. The parser reads CryEngine quaternions as `w,x,y,z`.
The housing query uses only XY. It ignores height and priority.
The polygon test supports concave shapes and uses the native half-open ray crossings.
Horizontal edges do not cross. Other edges use `minY < y <= maxY`.
Vertical edges include equality in X. Sloped crossings use strict `x < intersection` with float arithmetic.
For an axis-aligned rectangle, the minimum-X and minimum-Y edges are outside.
The maximum-X and maximum-Y edges are inside, except their minimum-coordinate endpoints.

Only `Group=1` shapes participate in the housing query.
The first matching shape supplies `value1`, including 0. An unbound first match masks later permits.
The parser rejects malformed housing shapes. The rule searches the full world because a polygon can cross a terrain zone boundary.
It applies only the selected area's category and ownership rules.
It does not intersect restrictions from overlapping areas.

The initial exact-client check read 381 bound polygons with 377 distinct area IDs in `main_world`.
Every bound polygon ID matched the server compact.
Zone 283 contains a real overlap at `(20300, 18200)` between areas 208 and 209.
Area 208 precedes area 209 in the root, NA, and CN files.
The corrected exact-client test selects area 208, permits category 1, and rejects category 7.
The earlier intersection rule rejected every category there. Review found this defect before publication.

### Native area query and order

These exact r208022 DLLs provide the geometry evidence. They are source DLLs, not process dumps.

| File | Size | SHA-256 |
| --- | --- | --- |
| `client/bin32/cryentitysystem.dll` | 944640 | `d1f0623a45c491888b9a2f3de54270d142d17afe2a63dd4c4ae95a3ea2a7377e` |
| `client/bin32/cry3dengine.dll` | 2876416 | `34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de` |

`cryentitysystem.dll` has PE timestamp `543cb80b`, base `32000000`, entry RVA `bc244`, and image size `101000`.
`cry3dengine.dll` has PE timestamp `543cb85c`, base `30000000`, entry RVA `236cb2`, and image size `38a000`.

The x2game placement function `39333e20` calls the entity-system housing query through virtual offset `c4`.
`CEntitySystem` vtable `320c63a0` resolves that call to `3201b7c0`, then `32098a30`.
The query scans the area vector from index 0 and returns the first matching shape's `value1` at offset `10c`.
It checks Group 1 at offset `44` and shape type 0 at offset `80`.
It calls `3208f090` with `ignoreHeight=true`.
Helpers `3208ebd0` and `3208eb90` supply the crossing boundaries.
`SetPoints` at `32094ad0` sets the valid-geometry byte at offset `109` for at least 3 points.
That byte is not an XML Enabled flag.

AreaProxy construction calls `32099b70` at `320837ed`.
`CreateArea` appends through `320248d0` to the live or pending vector.
`UpdateGridCompile` at `3209ada0` copies current areas, then pending areas, in order.
Its auxiliary float array stores fade distances. It does not sort areas.
`CheckAsyncGridCompileFinish` at `3209b540` updates cache indices through `3209b290` and copies the vector through `3209e920`.
Neither operation changes the order.
`cry3dengine` function `30163990` reads housing XML children in order and adds them to their cell XML.
Areas 208 and 209 share that cell, so their source order determines the native overlap result.

That caller names `housing_area.xml` under each zone's `client/` directory without a recursive region scan.
The lower-level client's region replacement remains unknown.
The current server discovers root and regional paths without a region setting.
In this client, zone 283's NA and CN files repeat only root shapes 208 and 209 exactly.
The server now reads root files first and removes exact duplicate shapes.
It compares ID, Group, ordered transformed points, Height, and Priority, not the area ID alone.
This keeps every distinct supplied shape and removes filesystem enumeration order from the overlap result.

The read-only Ghidra logs remain under the ignored `.tools-re/housing-496/native-*.log` paths.
No raw DLL, client XML, or decompiler output belongs in Git.

## Server behavior

The build check runs before tax certificates, money, or the design item change.
The check and house registration share the current persistence lock with house sales and removal.
The account count includes all owned houses across its characters.
The check uses the current house owner after a sale. It does not use the original builder.
The server does not add an Admin or Moderator bypass.

| Rejection | Error |
| --- | --- |
| No matching permitted area or category | `HouseCannotLoacateInvalidCategoryArea` (580) |
| Houseless area with an owned building | `HouseCannotOwnMoreHouselessCondition` (615) |
| The account already owns the prohibited category | `HouseCannotOwnMoreExistingCategoryCondition` (616) |
| The account reaches the category limit | `HouseCannotConstructInAreaByMaxConstructCount` (766) |

The loader retains `allowed_tax_delay_week` and `can_extend` for later tax work.
This construction change does not alter dates or remove current houses.
It needs no SQL or compact update, client patch, or new client asset distribution.

## Validation

The automated tests cover each rule, account scope, zero limits, concave polygons, coordinate conversion, rotation, and malformed positions.
They also cover ignored height and priority, zero-value masks, Group 1 selection, exact edges, and real overlapping client geometry.
The build-entry tests check that rejected requests preserve the design, both certificate types, money, and the house registry.
The source PR records the final full-suite and exact-client results after the native geometry correction.

Use these environment variables for the optional read-only exact-client test:

```sh
AAEMU_HOUSING_COMPACT=/absolute/path/to/server.sqlite3
AAEMU_HOUSING_CLIENT_ROOT=/absolute/path/to/extracted/client/root
```

The client root must contain `game/worlds/main_world/world.xml` and its extracted `housing_area.xml` files.
Use the repository client-pak tool to extract those files. Do not put the raw client files in Git.
Run `HousingPlacementRulesTests/RealClient_CompactAndGeometry_Agree` with the normal TUnit runner.
Without those paths, the runner marks this exact-client test as skipped. The synthetic tests still run in CI.

After deployment, test a valid farm, a wrong category, an outside-area request, and an account-wide ownership restriction.
Check a polygon boundary and a raised coastal area in the client.
Those gameplay checks need human validation after the published release.
