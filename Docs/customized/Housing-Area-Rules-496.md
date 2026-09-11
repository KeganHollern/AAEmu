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
It treats `Height=0` as an unbounded vertical area. A positive height starts at the lowest polygon point.
The polygon test includes the boundary and supports concave shapes.
It ignores unbound editor shapes with `value1=0` and rejects malformed bound shapes.
It searches the full world, because a housing polygon can cross a terrain zone boundary.
The current recursive source selection remains unchanged, including the zone 283 regional files.
Duplicate identifiers do not apply the same ownership rule twice.
Equal-priority overlaps retain all applicable restrictions. This is a conservative server rule, not a confirmed native tie-break rule.

The read-only exact-client test read 381 polygons with 377 distinct area IDs in `main_world`.
Every bound polygon ID matched the server compact.
The test does not establish the native height boundary or overlap tie-break behavior through a live client session.

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

The automated tests cover each rule, account scope, zero limits, concave polygons, coordinate conversion, rotation, height, and malformed positions.
The build-entry tests check that rejected requests preserve the design, both certificate types, money, and the house registry.
All 85 focused housing tests passed, including the exact-client test.
The full unit suite passed 2,631 tests. It skipped only the optional exact-client test, which passed in its separate run.

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
