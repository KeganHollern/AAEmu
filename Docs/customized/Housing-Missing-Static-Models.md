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
The general model resolver also uses the native brush fallback before binary parsing.
A malformed present file still reports its parsing error.

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

## Missing doodad models and prefab elements

Native `393b03b0` selects brush, prefab, animation, and vegetation model loaders.
Its brush constructor `393aebb0` uses the same engine stat-object loader as static brushes.
The general geometry resolver thus uses the authored default model for an absent brush file.
This also preserves valid siblings when one prefab brush child is absent.
A missing default model remains an error.

Native `39111210` reports a missing library element without a child object.
Its caller `391114f0` logs the failure and returns success with an empty prefab.
The same caller keeps an empty model when an animation object fails to load.
The resolver returns no physical parts for those cases.
`HasModelBounds=false` records that the empty asset cannot prove preview bounds.
It does not invent a render box for the absent object.

The full source contains 42650 main-world doodad spawns and 2298 template IDs.
Their base and phase fields contain 1666 distinct model paths.
None uses the `entity://` scheme.
The model scan after the missing-reference fixes reads 1608 paths.
The other 58 paths need the separate visual-only prefab and CDF readers.
The missing-reference work resolves 24 absent files, 16 absent prefab elements, and 2 bone-reference failures.
The local fixture records each path and its source templates in `world-doodad-models.json`.
`world-doodad-coverage.json` contains its geometry results.
The 15 resolver tests and 6 static resolver tests passed, including the full static client fixture.

## Native path separators

CrySystem `3650f4a0` calls `3650f0f0` to prepare an asset path.
That function calls `XlNormalizePath` at `33017a00` in `xlcommon.dll`.
Its helper `33018010` maps slash and backslash separators to one slash.
It removes repeated separators and preserves one trailing separator.
It does not remove `.` or `..` path elements.

The resolver now uses the same separator rule after the model URI scheme.
It preserves `prefab://`, `cgf://`, and the other model schemes.
This makes 5 authored banana-tree and bottle model paths match files in the exact archive.
A trailing slash after `.cgf` stays in the path and still follows native missing-file behavior.

The 18 resolver tests and 6 static resolver tests passed after this change.
The static fixture still finds exactly 8 absent brush models.
The path evidence is in `native-path1.log` through `native-path7.log` and their decompiles.

| Native module | SHA-256 |
| --- | --- |
| `xlcommon.dll` | `0e0881aa837553d7e307a5c6f9d886f3e82a0d72d94b6d666448aedaa94e2203` |
| `crysystem.dll` | `523ac57702ea9e17d7ced30e3701317169b80fa5cb37e2e876c33f9d2d112353` |
