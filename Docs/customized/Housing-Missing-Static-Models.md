# Missing static models in r208022

The main world references 8 absent brush CGFs. Each path serves 1 brush.
The files also remain absent after removal of repeated path separators.
These references do not represent an unsupported geometry format.

The native deferred brush path handles a failed model stream:

- `3015eb80` requests a stat-object stream through `3010c880`.
- `3011ed10` passes a null result after a failed CGF stream.
- `3010bf20` calls the cell callback with that result.
- `3015eca0` replaces null with the default stat-object pointer.
- `3010f000` loads `objects/default.cgf` and `objects/box_nodraw.cgf` for those pointers.
- `301009a0` does not create brush physics without a geometry proxy or a physical child.

The exact 6832-byte `default.cgf` and 3436-byte `box_nodraw.cgf` contain no physics proxies.
Both choices thus give the same empty collision result for this client.
The server uses the authored `box_nodraw.cgf` fallback.
It does not make a collision box from the missing brush bounds.

`CryWorldGeometryResolver` limits this behavior to a missing root CGF for a brush.
It keeps the fallback in its model cache.
It preserves voxel geometry that the world index already decoded.
Malformed geometry, unsupported formats, missing nested assets, and other object kinds still report their errors.
A missing fallback CGF also reports its error.
The regular model resolver does not change its missing-file behavior.

## Exact-client coverage

The static model scan found 4410 unique authored model paths.
The parser read all 4402 present CGFs without an unsupported format.
All authored vegetation models exist and parse.
The native solid-proxy filter keeps 116832 vegetation instances and removes 1444703 visual instances.
The final index contains 279447 instances: 162386 brushes, 229 voxels, and 116832 vegetation instances.

The exact-client test resolves every retained static model.
It checks the 8 missing brush fallbacks and both empty fallback CGFs.
The other tests check the cache, embedded voxel geometry, and error boundaries.
Set `AAEMU_HOUSING_GAME_PAK` to the r208022 archive for the exact-client test.

## Provenance

The native addresses refer to `cry3dengine.dll` at image base `30000000`.
Its SHA-256 is `34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.

| Asset | SHA-256 |
| --- | --- |
| `game/objects/default.cgf` | `453caf22089182ad725c3cbb3580e48fee25ece5b1f09fa4ead4a6ae1efb6265` |
| `game/objects/box_nodraw.cgf` | `00045be8bf80952a4779df9332f6ffb29af9d47c58a60457004f970c9ab7b160` |

The local evidence directory is `.tools-re/housing-20260912/limits-farms` in the cluster workspace.
It contains `native-missing1.log` through `native-missing11.log`, the matching decompiles, and `default-geometry.log`.
It also contains `world-model-coverage.json` with the exact 8 absent paths and instance counts.
This check does not include a manual client test.
