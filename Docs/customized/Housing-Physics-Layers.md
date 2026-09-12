# Native physics layers for housing

This record covers the model layer filter for housing placement and surface
rays. It does not change the general server physics engine.

The exact r208022 binary is `client/bin32/cry3dengine.dll`, with image base
`0x30000000` and SHA-256
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.
Native `3003b930` loads the 4 compiled geometry slots. `300343d0` assigns
their types as `0x1000 + slot`. `300249f0` preserves the geometry and its type.

Native `30030f20` identifies itself as `CStatObj::Physicalize` through its log
text. It confirms these query roles:

| Statobject parts | Ray geometry | Placement overlap geometry |
| --- | --- | --- |
| Solid layer `0x1000` only | Solid | Solid |
| Ray layer `0x1001` only | Ray | None |
| Obstruct layer `0x1002` only | None | None |
| One solid and one other layer | Other layer | Solid |
| Other ordinary combinations | Solid and ray layers | Solid |
| Foliage with spines, at most one solid, and one non-solid layer | Solid, if present | Solid, if present |
| Foliage with spines, at most one solid, plus ray and obstruct layers | Solid, if present | Solid, if present |

The ordinary branch assigns `0x8000` to ray geometry and `0x4000` to obstruct
geometry. In the two-part branch, `geom_proxy=0x20000` attaches the solid
geometry as a separate collision proxy. The other part remains the ray shape.
The foliage branch assigns `0x302000`, `0x104000`, or `0x202000` to its extra
parts. These flags contain neither the default ray bit nor placement bits 0
and 1. A fourth slot does not get a query role in the ordinary branch.

The filter needs the part types and spine count for each native statobject.
Do not combine all parts of a prefab into one group. A prefab can contain
several independent statobjects with different proxy pairs.

Exact X2Game native `3903c5a0` requests `PrimitiveWorldIntersection` with entity
mask `0x1f`, `geomFlagsAll=0`, and `geomFlagsAny=3`.
The entity mask includes static, sleeping rigid, rigid, living, and independent
entities. It excludes terrain. The call supplies no skipped entity array.
Surface rays have a separate entity mask and use default ray geometry.

`CryGeometryLayerRules.GetHousingUsage` returns the roles for those housing
queries. Scene code still applies each object's physical state and transform.
This filter does not establish living actor dimensions or convert decorative
water surfaces into solid objects.

The 5 focused tests passed. They cover single layers, both proxy roles,
three-part objects, foliage spines, and independent statobject groups.
The Release build passed with 0 errors. The parent housing record
contains integration test results and deployment status.
