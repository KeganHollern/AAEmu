# Authored vegetation for housing collision

The world index reads vegetation from both `object.dat` and `vegetation.dat`.
It uses the authored model group, position, scale, rotation, bounds, and material override.
The runtime resolves model geometry with the shared CGF reader.

The main world contains 82956 typed vegetation records and 1478612 streamed records.
The native loader skips 33 streamed records that reference deleted group `737`.
The complete index retains 1561535 vegetation instances before the optional model filter.
The model filter can remove groups with no solid physics proxy before the index stores their records.
It runs once for each distinct model URI.

## Native data contract

The original r208022 `cry3dengine.dll` SHA-256 is
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.

`300f9420` reads `vegetation.xml`.
`300f76c0` reads its group ID, model filename, material name, and terrain alignment flag.
`300f8270` installs each group at its authored ID in the native statobject table.

`301f5a90`, `_LoadVegetation`, reads a 64-byte body.
An `object.dat` vegetation record adds a 4-byte type prefix.

| Body offset | Field |
| --- | --- |
| `0` | AABB minimum, 3 floats |
| `12` | AABB maximum, 3 floats |
| `39` | Position, 3 floats |
| `51` | Uniform scale, float |
| `55` | Group ID, signed 32-bit integer |
| `60` | Rotation, unsigned byte |

The loader clamps scale to `0.1..5`.
`300f4390` converts rotation with `byte * (360 / 255)` degrees.
The loader adds the cell origin for typed records.
For streamed records, it adds the 256-meter sector origin.

`30145910` reads the cell file `client/vegetation.dat`.
Its header contains version `1`, 16 signed offsets, and 16 signed byte lengths.
The header size is 132 bytes.
Sector order is `x * 4 + y`.
`301f86f0` reads each sector as 64-byte bodies and uses the same `_LoadVegetation` function.
The older individual `vegetation/XX_YY.dat` files are not inputs to this native load path.

## Terrain alignment and collision flags

`300f4390`, `CVegetation::CalcMatrix`, applies the group terrain alignment flag.
It gets a terrain normal from `300c23d0`.
The sample distance is half the authored world AABB diagonal plus `0.05` meters.
The four height samples are `(x-r,y-r)`, `(x,y+r)`, `(x+r,y)`, and `(x+r,y+r)`.
The helper preserves the native cross-product calculation and the order of rotation, alignment, and scale.
It keeps the instance translation fixed.

`CryVegetationGeometry.ResolveTransform` computes this alignment for query candidates.
The runtime supplies the exact terrain height reader.
Missing terrain prevents a geometry result.

`300f5b40`, `CVegetation::Physicalize`, gives ordinary solid flags to physics slot `0x1000`.
Its extra foliage proxies use flags `0x300000`, `0x304000`, `0x104000`, `0x200000`, or `0x202000`.
Those flags contain neither the housing overlap mask `3` nor the ray flag `0x8000`.
`CryGeometryLayerRules.GetVegetationUsage` preserves this distinction.
The general statobject rule does not apply to these vegetation instances.

The native dispatch also confirms render-node type `14` as `DistanceCloud`.
`AutoCubeMap` is type `16`.
