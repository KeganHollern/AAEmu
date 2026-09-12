# Separate static world files

The r208022 client uses separate files for deferred brushes and large
objects. These files share objects with `object.dat`. A consumer must
select one brush source and remove duplicate water volumes.

The native evidence uses `cry3dengine.dll` SHA-256
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.
Addresses use image base `0x30000000`.

## Deferred brushes

`CryDeferredBrushFile.Read(brush, statobjs, materials)` reads `brush.dat`,
`statobjs.dat`, and `materials.dat`. It returns `CryDeferredBrushInstance`
records with a parsed brush, model path, material path, and sector X/Y.
Bounds and matrices in each returned brush use cell-local coordinates.
The caller then adds the world cell origin. All streams remain open.

`brush.dat` contains:

- A 32-bit version, equal to 1 in all 1205 main-world files.
- 256 sector offsets, starting at byte 4.
- 256 sector byte lengths, starting at byte 1028.
- 128-byte brush records, starting at byte 2052.

Sector index is `x * 16 + y`. Each sector is 64 m wide. The brush record
matches the ordinary 132-byte record without its four-byte type prefix.
`statobjs.dat` and `materials.dat` each contain a 32-bit count followed by
256-byte names. These tables are independent of the `object.dat` tables.

Native `30161530` selects the three files. `30157380` reads the two sector
tables. `3015FF10` reads 128-byte records, checks their model/material
indices, and supplies the sector origin. `301F3400` calls the ordinary
brush constructor `301F2CA0`, which applies the origin to bounds and the
matrix translation. `301610D0` reads the deferred material names in
256-byte records.

The full main-world scan found 162386 deferred brushes and no invalid
versions, sector ranges, or model/material references. All 162386 match
`object.dat` brushes by model path, material path, and matrix values rounded
to 1 mm. They are alternate records for the same brushes. Use deferred
brushes when that file exists. Do not append both sources.

## Large objects

`CryBigObjectsFile.Read(stream, optionalReaderFactory)` returns model paths,
material paths, and parsed objects. The default factory supports the
current brush, voxel, water, road, and fixed-record readers. A consumer can
supply the same factory that it uses for `object.dat`.

`big_object.dat` contains the same two path tables as `object.dat`:
a 32-bit count followed by 260-byte entries, with four flag bytes and a
256-byte name. Flat typed records follow the tables. No octree header
separates those records.

Native `30148FC0` opens the file. `30148D90` reads its path tables through
`3014B9D0` and `3014A5A0`, then dispatches the flat records through
`301F8420`.

The full main-world scan found 1175 large objects, all water volumes.
Of those, 556 have identical bounds and complete water payloads in
`object.dat`. The other 619 are new or different. This comparison includes
both contour arrays. It excludes render metadata and the table-specific
material index. The consumer must include the new water geometry and
remove the duplicate copies. Volume ID alone is not a complete geometry key.

## Checks

15 focused tests cover the independent tables, sector coordinates,
cell-local transforms, malformed ranges and references, flat record
boundaries, custom factories, and truncated water contours.

The exact-client test reads `main_world/cells/026_008/client/` and finds
330 deferred brushes and 1 large water volume. Fixture SHA-256 values are:

| File | SHA-256 |
| --- | --- |
| `brush.dat` | `a8aef1cc2b9daf1544b910ad605aa8623b5a8ef6ff8e4cf8baeb4d200c6dc1d8` |
| `statobjs.dat` | `dcf678a89c4ba630a808b382919a6b80deeebd47c1b7b980f6eb577b3d539d5b` |
| `materials.dat` | `5b0761cda56282867335f23a84e460e8431ea53888f4bf54c02d3efb674f050c` |
| `big_object.dat` | `c198bce25e6af6b2b1868bbccbfb3627e323ef88b287a33e5196c59f3e6e38b7` |

Set `AAEMU_STATIC_WORLD_CLIENT_ROOT` to the extracted directory above
`game/`, then run:

```sh
dotnet run --project AAEmu.UnitTests -c Release --no-build -- --treenode-filter '/*/*/CrySeparateWorldFilesTests/*' --no-progress
```

Local scan evidence is in `.tools-re/housing-20260912/permissions/`:
`deferred-brush-scan.json`, `separate-object-comparison.json`, and
`big-water-comparison.json`. No client data enters Git.
