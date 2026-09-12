# World objects for house placement

The index now uses `brush.dat` when that native source exists.
It does not also add the copies from `object.dat`.
The complete main-world comparison found the same 162386 authored brushes in both sources.
The deferred source supplies sector-local coordinates and its own model and material tables.
The parser adds the sector origin before the index adds the cell origin.
The index uses `object.dat` brushes when the deferred source does not exist.

The index also reads the flat `big_object.dat` records through the common typed reader.
The main-world large files contain water volumes and no solid geometry.
Water inclusion remains the responsibility of the world water model.
See `Housing-Separate-World-Files.md` for the exact native formats and comparison evidence.

## Authored voxel geometry and materials

The index also reads all 229 voxel objects in the main world.
Each voxel uses the serialized CryPhysics mesh from its first compressed CGF LOD.
The shared CGF reader preserves the authored node transforms and triangle material IDs.
The voxel instance adds its object.dat transform and the cell origin.
The index does not substitute the voxel bounding box for its collision mesh.

Native `_LoadVoxelObject` at `301f5e30` reads voxel version `5` and adds the cell origin to its matrix.
`30196930` reads 32 terrain surface names, with 64 bytes for each name.
It maps those names through the cell terrain surface table.
`3019d570` retains the compressed LOD blocks.
`30192d40` loads their CGF content, and `3019bfa0` uses the serialized physical mesh when it exists.
All 229 main-world first LODs contain mesh, node, and physical mesh chunks.

`CryWorldObjectInstance.Asset` holds this decoded voxel geometry.
`TerrainSurfaceNames` preserves the authored material index table.
The material adapter must map these terrain names before it applies surface rules.

The second path table in object.dat contains material overrides.
`ObjectsFile.MaterialPathsList` now retains those paths.
Brush instances preserve their selected path in `CryWorldObjectInstance.MaterialPath`.
The native brush loader is `301f2ca0`.

The exact-client index test reads 1205 files, 162386 brush instances, and 229 voxel meshes.
Vegetation and other streamed cell files need their separate native loaders.

## Brush index

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
