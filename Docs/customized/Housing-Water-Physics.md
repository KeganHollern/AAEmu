# Housing water physics for r208022

Housing placement uses `HousingWaterGeometry`, separate from movement water.
It reads authored physics contours without the legacy 5000 m² ingest cutoff.
`HousingGeometryAssets` caches the result per world template.

## Native evidence

The checked client Cry3DEngine SHA-256 is
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.
Native exports remain under the ignored
`.tools-re/housing-20260912/permissions` and `placement` directories.

| Native function | Confirmed contract |
| --- | --- |
| `39331d20` | Housing samples terrain and calls `I3DEngine::GetWaterLevel` at the sample. |
| `301491a0` | Each cell loads `object.dat` before its `big_object.dat` path. |
| `301f6ab0`, `301fbb10` | A world registry uses the full 64-bit VolumeId. Duplicate Area nodes stop loading. Duplicate River nodes add no physics contour. Only Area/River create these physics areas. |
| `301f6ab0` | Every vertex receives the cell's X/Y offset. Area plane D uses the first render vertex Z. River plane D starts at the render AABB maximum and shifts the highest projected render vertex to the maximum Z. |
| `300ea010`, `300eaa00` | Area and River physics contours project vertically onto the adjusted fog plane. Both need at least 4 vertices. Rivers also need an even count. Raw nonplanar heights do not describe the final physics surface. |
| `300e73b0` | Physicalization calls CreateArea with the projected contour, lower extent `min(0,-Depth)`, and upper extent `10` (`3024c8bc=0x41200000`). |
| `35054890` | CreateArea uses a fitted plane basis. A planar area without flow adds `0.01` to its upper extent (`351fa42c=0x3c23d70a`). Rivers supply a flow field and a triangle mesh. |
| `35050f30`, `351df070` | Area containment checks strict depth limits and the contour. River meshes also require the query ray along the mesh normal to reach an outward-facing surface. |
| `35053450`, `35052b90`, `3504ef50` | Water registers at the head of its medium list. The first local match replaces global buoyancy and clears its medium marker to -1. Later matches append, up to 4 slots. The query takes the maximum of those 4 newest intersecting volumes. There is no smallest-footprint policy. |
| `3012b2e0` | The height is `point.Z - dot(normal, point - planeOrigin) * normal.Z`. The result cannot be below the ocean surface. This is a normal projection, not a vertical intersection with a sloped plane. |

The server uses the authored ocean baseline. Renderer-dependent waves do not
exist in the server process. The cache uses cell Y/X order, then record order,
with object data before big-object data. A client's prior cell load history
can give it different selected volumes when more than 4 overlap. The server does not keep
per-client cell streaming history.

## Dynamic prefab water

`CryGeometryResolver.LoadWater(path)` reads WaterVolume metadata without meshes
or animation poses. It keeps XML object order and child transforms.
`CryGeometryAsset.WaterVolumes` exposes the same authored records to geometry callers.
The caller selects the current doodad phase model, with the base-model fallback,
or the current house construction model before it reads this metadata.

| Native function | Dynamic contract |
| --- | --- |
| `3910f570`, `3910cd10` | The prefab loader creates WaterVolume children in object order. It needs at least 4 points. It reads VolumeDepth and creates a water render node. |
| `39104ec0`, `39101300` | Child scale, rotation, and position compose with the prefab instance transform. |
| `390ff370`, `39110b40` | The final Comment named origin supplies a position offset. Post-process subtracts it from every child's translation, then rebuilds every child in object order. The origin comment itself is not a child object. |
| `3910b600`, `3910bb90` | The update transforms raw points, adds the height offset, and forms a plane from transformed +Z through the first point. It calls Area setup, contour setup, and Physicalize. |
| `300e85a0`, `300ea010`, `300e73b0` | Render and physics vertices project vertically to that plane. Plane normal Z must exceed `0.0001`. Depth is the authored depth plus the height offset, without scale multiplication. |
| `39103020`, `3001af60`, `300e6650`, `300eca50` | A WaterVolume supplies model bounds. The render node subtracts its AABB center from its world bounds. The prefab getter then applies the child transform. |

The water load does not read `bVisible` or `HiddenInGame`. Its physical
registration has no visibility condition. These XML flags must not remove
the water area from the housing query.

`CryWaterVolumeInstance` holds an ordered set of water children, a parent
world transform, and a height offset. The housing caller keeps the offset at
zero for phase-start physics. A visual rise from `DoodadFuncWaterVolume` does
not change this input without a native area rebuild.

The query overload accepts active instances in oldest-to-newest server order.
It checks newest instances and children first, followed by the static cache.
Dynamic and static water share the 4-result limit. The caller builds this
list once per construction request. A phase change or removed instance
changes the next list and leaves no stale area in the static cache.
This server order does not claim to reproduce each client's cell load history.

`CryPrefabWaterVolume.GetModelBounds(parentTransform)` also supports the native
water bounds calculation for a supplied parent pose. Cached asset bounds use
the identity parent pose. Physical water queries always use the supplied
instance pose directly.

The exact fixture is `game/prefabs/e_falcony_plateau.xml`, SHA-256
`c7a4bfdef4debff144c4b25f5b0667958865fadd06eb77695e64173176513530`.
Prefab `e_falcony_plateau.water_b` has 8 points and depth 30.
At the main-world doodad position `(22922.07,9493.238,527.0209)`, its surface
is `547.9945` with an identity parent rotation. The fixture's comment object
has `Name="Comment220"` and `Comment="origin"`. Native `3910f87d` reads the
Name attribute, so this object does not rebase the prefab.
Set `AAEMU_PREFAB_WATER_CLIENT_ROOT` to the extracted root above `game/`
and run `CryPrefabWaterTests` for this fixture and the pure dynamic checks.

## Client scan and fixture

The complete main-world scan found 5758 object-file water records with 455 IDs.
The 1175 big-object records use 134 IDs. They add only 1 ID absent from object
files. The earlier count of 619 different complete records includes alternate
render segments and does not mean 619 new physics volumes.

`main_world/cells/020_031/client/big_object.dat` adds Area ID
`4639442551196662424`. Its 6-point contour has surface Z `298` and depth `10`.
The fixture checks that `(21000,32800,297)` changes from ocean-only to that area.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `020_031/client/object.dat` | 17243 | `8073daa6319e44aaf500345bf2e312a20b42b350f64548188556c8a45a9b673b` |
| `020_031/client/big_object.dat` | 1069 | `335c0a546117bbfe403d29f6b7e42ef13cc14e58a933365587eaf4a11a671e48` |

The focused tests cover small authored areas, strict depth, normal projection,
raw river heights, duplicate IDs, overlap order, ocean precedence, cell offset,
file order, and the exact missing area. Set `AAEMU_STATIC_WORLD_CLIENT_ROOT` to
the extracted client root for the optional exact-file test. No extracted
client assets belong in Git.
