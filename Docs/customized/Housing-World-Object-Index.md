# World objects for house placement

This change provides the broad phase for static brush collision queries in
cluster issues #307 and #309. It uses the current `ObjectsFile` parser and
the exact r208022 `object.dat` data.

`CryWorldObjectIndex.Load(world)` reads brush metadata from every cell in that
world template. Each immutable record has an asset URI, world transform,
world bounds, and source file. The index contains no model geometry.
The caller keeps one index per world template and loads model geometry only
for the instances that a bounds query returns.

The index covers each brush's full authored bounds. A query can find a brush
that extends across a cell border. It does not assume a fixed neighbor radius.
The 64-meter buckets only limit search work. They do not change collision
rules. Queries include boundary contact and all 3 position axes.
They return each matching instance once, in a repeatable order.

`object.dat` stores the brush matrix and bounds in cell-local game XYZ axes.
The index adds the cell offset once. It transposes the 3-by-4 CryEngine matrix
for the row-vector convention in `System.Numerics.Matrix4x4`. Rotation, scale,
translation, and the Z coordinate remain in the transform.
The index does not use the bounds as a final collision result.

The `ObjectsFile` stream overload permits a caller to use a separate client
source. `HasUnparsedObjects` reports a partial block parse without a change
to the old parser result. The index rejects partial or unreadable data.
It also rejects invalid asset references instead of omitting a possible
collision object.

The fixture test used the exact local r208022 `client/game_pak` on 2026-09-12.
All 1205 `main_world` object files parsed without an unreadable block.
The index contains 162386 brush instances. This test took about 2.4 seconds.
The 6 index tests passed. They also cover transformed brush points, cell
borders, duplicate buckets, negative coordinates, Z separation, world file
selection, and truncated data. The Release build passed with 0 errors.
All 3258 unit tests passed with 0 skips. The test run included the exact
client archive, the real compact, and the housing area fixture.

Set `AAEMU_HOUSING_GAME_PAK` to the exact game archive to run the optional
full-world fixture. Native model collision queries and terrain queries remain
in separate components. The housing scene connects those components to this
index. This record does not claim a manual client test or deployment.
