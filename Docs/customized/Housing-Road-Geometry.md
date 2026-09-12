# Road records and housing collision

The r208022 client does not use roads as solid housing blockers. A road can
change the material of a terrain contact. Its visual surface does not add
an independent solid surface to the housing query.

The native evidence comes from `client/bin32/cry3dengine.dll`, SHA-256
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.
Addresses use image base `0x30000000`.

| Address | Evidence |
| --- | --- |
| `301f8420` | The object loader dispatches type 13 to `_LoadRoadObject` at `301f62d0`. |
| `301f4580` | `SRoadChunk_NEW` has a 63-byte packed header. Its 32-bit vertex count is at header offset `0x27`. The type prefix adds 4 bytes. |
| `301f62d0` | The loader consumes the 63-byte header and `count * 12` vertex bytes. It adds the 1024 m cell origin to the points. |
| `301f3ed0` | The shared render header defines world bounds, render flags, view-distance ratio, LOD ratio, and internal flags. |
| `30209560` | The road constructor installs vtable `30272188`. |
| `30272188 + 0xC4` | The road's `Physicalize` slot points to the empty shared method `3001aa20`. |
| `30208620` | The separate `CRoadRenderNode::Physicalize` helper creates a static entity with `pe_geomparams.flags = 0x02000000`, `geom_mat_substitutor`. It sets no collision-type bits. No call to this helper exists in the client text section. |

The geometry flags at `30208620` occupy `pe_geomparams + 0x38`. The value
does not contain the player/default collision bits in housing overlap
mask 3. It also does not contain the ray collision bit `0x8000`.
No road blocker mesh enters the server housing index.

`ObjectDataType13Road` now reads the full 32-bit count at record byte 43.
The old byte read shortened records with more than 255 points and lost the
position of subsequent records. The reader rejects truncated or invalid
counts. It also exposes the native bounds, render flags, texture ranges,
and material ID.

The tests cover a 300-point record followed by a brush, invalid counts,
short headers, and an exact client record. The exact record comes from
`main_world/cells/011_014/client/object.dat`, offset 227177, length 859.
It contains 66 points and material ID 16. Set `AAEMU_ROAD_RECORD` to the
extracted record, then run:

```sh
dotnet run --project AAEmu.UnitTests -c Release --no-build -- --treenode-filter '/*/*/ObjectDataType13RoadTests/*' --no-progress
```

Other static render types need separate treatment. Native dispatch
`301f8420` proves type 9 is a decal and type 14 is a distance cloud.
AutoCubeMap is type 16. The decal and distance-cloud loaders create render
nodes and do not add solid physics geometry. Water volumes use physics
area entities, outside the housing overlap entity mask `0x1F`.

The decal vtable is `30257848`. Its `GetPhysics` slot `+0xB8` points to
`30056760`, which returns null. Its `Physicalize` slot `+0xC4` points to the
empty `3001aa20`. The distance-cloud vtable is `3025CC48`; its `Physicalize`
slot points to the same empty method.
