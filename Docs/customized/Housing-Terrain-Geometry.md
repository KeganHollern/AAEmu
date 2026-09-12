# Terrain geometry for housing

`CryTerrainGrid` reads the r208022 terrain heightfield for housing physics.
It keeps float heights and terrain holes. It does not change `WorldCell` or
the global height cache.

## Exact client evidence

The local `client/bin32/cry3dengine.dll` has SHA-256
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.
Addresses below use its preferred image base `0x30000000`.

| Address | Evidence |
| --- | --- |
| `300db920`, `300dbce0` | `CTerrainNode::Load_T` accepts node version 5. It copies the ushort samples unchanged. It reads float offset/range and rebases node XY bounds to the streamed region. |
| `300db400` | `CTerrainNode::GetData` writes the same version 5 node header and samples. After the samples it writes `log2(sector units)` error floats, the used surface list, and 36 reserved bytes. |
| `3022b4b0` | `STerrainNodeChunk` describes the 45-byte header and its field offsets. |
| `300c8b60` | Height is `offset + (sample & 0xFFE0) * range`. Lower-resolution nodes use bilinear interpolation to get heights at full-resolution vertices. |
| `300ca120` | The low 5 bits are the surface ID. The used surface list does not remap these IDs. Lower-resolution nodes use the lower source sample for the surface. |
| `300ca620`, `300cda10` | The physics callback maps surface 31 to hole sentinel `0x7FF`. The heightfield uses float height callbacks. |
| `300c9520` | `CHeightMap::RayTrace` skips both triangles when the lower corner surface is 31. It uses the fixed diagonal between `(1,0)` and `(0,1)` and upward-facing triangles. |
| `300c91b0` | The height query uses the same fixed diagonal. It does not interpolate across the complete square as one bilinear surface. |
| `301234e0`, `300cabc0`, `300ca1f0`, `300c88f0` | `I3DEngine` slot `+0x204` shifts integer metre coordinates by `log2(UnitSize)`, then reads that full-resolution vertex. It ignores holes. Lower-resolution nodes interpolate packed heights before the range and offset conversion. |

The old `NodeCell` mask `0xFFF0` keeps one surface bit in the height.
Its 5 cm conversion and the later ushort world cache do not represent this
native float geometry. The new helper does not use those conversions.

## Reader and query contract

`Read(Stream, Vector2 cellOrigin)` accepts little-endian heightmap version 24
with node version 5. The caller supplies the world origin of the cell.
The reader subtracts the file root XY origin from each node before it adds
the supplied origin. It leaves the stream open.

`GridSize` counts vertices, including the final edge. A normal 1024 m cell
has 513 vertices per side at a `UnitSize` of 2 m. `Bounds` keeps the native
root height bounds. `SampleHeight(worldX, worldY)` returns the triangle
height, or `NaN` for a hole or unavailable data.

`SampleRawHeight(int worldX, int worldY)` matches the vertex query at
`I3DEngine +0x204`. It floors integer metre coordinates to the unit grid.
It returns the height under holes. Missing data returns `NaN` so the server
can deny placement. The native wrapper returns zero for unavailable data.
The housing water check at `x2game!39331d20` uses this vertex query.

`Raycast(origin, direction, maxDistance, out hit)` normalizes the direction.
It returns distance in metres, the upward terrain normal, the cell surface
ID, and a triangle index. It returns `Indeterminate` if unavailable data can
hide a nearer hit. A hole returns `Clear`. The caller owns terrain identity
and world-cell surface-to-material mapping.

## Checks

The focused tests cover packed surface bit 4, direct surface IDs, float
precision, both triangles, holes, lower-resolution nodes, sector seams,
oblique rays, finite distance, missing data, and malformed input.

The exact-client fixture is:

`game/worlds/main_world/cells/021_009/client/terrain/heightmap.dat`

Its SHA-256 is
`0eca51f3b86deab8c8884d53860d14a3f0fbc4d85d96354f61dfc17ed97b3e8b`.
The file contains 81 interior hole squares. The root starts at `(4096,6144)`.
A leaf starts at `(4992,6912)`. Its source sample `(8,18)` is a hole. After rebasing
the root to zero, the test ray at `(913,805)` passes through that hole.
The adjacent solid ray at `(911,805)` hits height `393.5210876464844`.

Extract the fixture with the repository client-pak skill. Set
`AAEMU_TERRAIN_CLIENT_ROOT` to the directory above `game/`, then run:

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release --no-restore -m:1
dotnet run --project AAEmu.UnitTests -c Release --no-build -- --treenode-filter '/*/*/CryTerrainGridTests/*' --no-progress
```

Native exports are local research artifacts under
`.tools-re/housing-20260912/permissions/`. The tests keep synthetic binary
fixtures in source. No extracted client asset enters Git.
